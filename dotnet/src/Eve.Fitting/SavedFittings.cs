using System.Text.Json;
using Eve.Data;
using Npgsql;

namespace Eve.Fitting;

/// <summary>Who keeps a saved fitting: the character, the corporation or the alliance (EveJS <c>OWNER_SCOPE</c>).</summary>
public enum FittingOwnerScope : short
{
    Character = 1,
    Corporation = 2,
    Alliance = 3,
}

/// <summary>One fitting line: an item, the flag it goes to and the quantity (EveJS <c>fitData</c> tuple).</summary>
public sealed record FittingItem(int TypeId, int Flag, int Quantity);

/// <summary>A fitting as the client saves it, before the store gives it an id.</summary>
public sealed record FittingDraft(int ShipTypeId, string Name, string Description, IReadOnlyList<FittingItem> FitData);

/// <summary>A saved fitting: the draft, its id, its owner and the time it was saved (a FILETIME).</summary>
public sealed record SavedFitting(long FittingId, long OwnerId, FittingDraft Draft, long SavedAtFileTime);

/// <summary>
/// The saved fittings of characters, corporations and alliances, in PostgreSQL in the fitting module's schema (migration 0005). The limit of an owner
/// (EveJS <c>MAX_CHAR_FITTINGS</c> 500, <c>MAX_CORP_FITTINGS</c> and <c>MAX_ALLIANCE_FITTINGS</c> 600) is checked inside the same transaction as the insert.
/// </summary>
public sealed class PgSavedFittingStore
{
    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public PgSavedFittingStore(NpgsqlDataSource source, string schema = PgModuleStateStore.DefaultSchema)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
        {
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        }
        _source = source;
        _schema = schema;
    }

    /// <summary>The fittings an owner keeps, oldest first (EveJS <c>getOwnerFittingsResponse</c> orders them by id).</summary>
    public async Task<IReadOnlyList<SavedFitting>> ListAsync(FittingOwnerScope scope, long ownerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT fitting_id, ship_type_id, name, description, fit_data::text, saved_at FROM {_schema}.saved_fitting WHERE owner_scope = $1 AND owner_id = $2 ORDER BY fitting_id",
            connection)
        {
            Parameters = { new() { Value = (short)scope }, new() { Value = ownerId } },
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var fittings = new List<SavedFitting>();
        while (await reader.ReadAsync(cancellationToken))
        {
            fittings.Add(Read(reader, ownerId));
        }
        return fittings;
    }

    /// <summary>Saves drafts for an owner in one transaction. Returns the new ids in order, or null when the owner would go over <paramref name="limit"/>.</summary>
    public async Task<IReadOnlyList<long>?> SaveManyAsync(
        FittingOwnerScope scope, long ownerId, IReadOnlyList<FittingDraft> drafts, int limit, long savedAtFileTime, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // serialises the saves of one owner, so the count and the inserts agree
        await Run(connection, transaction, "SELECT pg_advisory_xact_lock($1)", [OwnerLockKey(scope, ownerId)], cancellationToken);
        var existing = await Scalar(connection, transaction, $"SELECT count(*) FROM {_schema}.saved_fitting WHERE owner_scope = $1 AND owner_id = $2", [(short)scope, ownerId], cancellationToken);
        if (Convert.ToInt64(existing) + drafts.Count > limit)
        {
            return null;
        }
        var ids = new List<long>(drafts.Count);
        foreach (var draft in drafts)
        {
            var id = await Scalar(
                connection,
                transaction,
                $"INSERT INTO {_schema}.saved_fitting (owner_scope, owner_id, ship_type_id, name, description, fit_data, saved_at) VALUES ($1, $2, $3, $4, $5, $6::jsonb, $7) RETURNING fitting_id",
                [(short)scope, ownerId, draft.ShipTypeId, draft.Name, draft.Description, FitDataJson(draft.FitData), savedAtFileTime],
                cancellationToken);
            ids.Add(Convert.ToInt64(id));
        }
        await transaction.CommitAsync(cancellationToken);
        return ids;
    }

    /// <summary>Replaces a fitting's draft. False when the owner has no fitting of that id (EveJS <c>FITTING_NOT_FOUND</c>).</summary>
    public async Task<bool> UpdateAsync(FittingOwnerScope scope, long ownerId, long fittingId, FittingDraft draft, long savedAtFileTime, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"UPDATE {_schema}.saved_fitting SET ship_type_id = $4, name = $5, description = $6, fit_data = $7::jsonb, saved_at = $8 WHERE owner_scope = $1 AND owner_id = $2 AND fitting_id = $3",
            connection)
        {
            Parameters =
            {
                new() { Value = (short)scope }, new() { Value = ownerId }, new() { Value = fittingId }, new() { Value = draft.ShipTypeId },
                new() { Value = draft.Name }, new() { Value = draft.Description }, new() { Value = FitDataJson(draft.FitData) }, new() { Value = savedAtFileTime },
            },
        };
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>Renames a fitting. False when the owner has no fitting of that id.</summary>
    public async Task<bool> RenameAsync(FittingOwnerScope scope, long ownerId, long fittingId, string name, string description, long savedAtFileTime, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"UPDATE {_schema}.saved_fitting SET name = $4, description = $5, saved_at = $6 WHERE owner_scope = $1 AND owner_id = $2 AND fitting_id = $3",
            connection)
        {
            Parameters =
            {
                new() { Value = (short)scope }, new() { Value = ownerId }, new() { Value = fittingId },
                new() { Value = name }, new() { Value = description }, new() { Value = savedAtFileTime },
            },
        };
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>Deletes the owner's fittings among the ids. Returns the ids that were there (EveJS <c>deleteManyFittings</c>).</summary>
    public async Task<IReadOnlyList<long>> DeleteAsync(FittingOwnerScope scope, long ownerId, IReadOnlyCollection<long> fittingIds, CancellationToken cancellationToken = default)
    {
        if (fittingIds.Count == 0)
        {
            return [];
        }
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"DELETE FROM {_schema}.saved_fitting WHERE owner_scope = $1 AND owner_id = $2 AND fitting_id = ANY($3) RETURNING fitting_id",
            connection)
        {
            Parameters = { new() { Value = (short)scope }, new() { Value = ownerId }, new() { Value = fittingIds.ToArray() } },
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var deleted = new List<long>();
        while (await reader.ReadAsync(cancellationToken))
        {
            deleted.Add(reader.GetInt64(0));
        }
        return deleted;
    }

    /// <summary>A saved fitting by id, whoever owns it (EveJS <c>findFittingByID</c>), or null when there is none.</summary>
    public async Task<SavedFitting?> FindAsync(long fittingId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT fitting_id, ship_type_id, name, description, fit_data::text, saved_at, owner_id FROM {_schema}.saved_fitting WHERE fitting_id = $1",
            connection)
        {
            Parameters = { new() { Value = fittingId } },
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader, reader.GetInt64(6)) : null;
    }

    /// <summary>The owner of a saved fitting (any scope), or null when there is none.</summary>
    public async Task<(FittingOwnerScope Scope, long OwnerId)?> OwnerOfAsync(long fittingId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT owner_scope, owner_id FROM {_schema}.saved_fitting WHERE fitting_id = $1", connection)
        {
            Parameters = { new() { Value = fittingId } },
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ((FittingOwnerScope)reader.GetInt16(0), reader.GetInt64(1)) : null;
    }

    private static long OwnerLockKey(FittingOwnerScope scope, long ownerId) => ((long)scope << 56) ^ ownerId;

    private static string FitDataJson(IReadOnlyList<FittingItem> items) =>
        JsonSerializer.Serialize(items.Select(item => new[] { item.TypeId, item.Flag, item.Quantity }));

    private static SavedFitting Read(NpgsqlDataReader reader, long ownerId)
    {
        var items = new List<FittingItem>();
        using (var document = JsonDocument.Parse(reader.GetString(4)))
        {
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                items.Add(new FittingItem(entry[0].GetInt32(), entry[1].GetInt32(), entry[2].GetInt32()));
            }
        }
        return new SavedFitting(
            reader.GetInt64(0),
            ownerId,
            new FittingDraft(reader.GetInt32(1), reader.GetString(2), reader.GetString(3), items),
            reader.GetInt64(5));
    }

    private static async Task Run(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, object?[] values, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<object?> Scalar(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, object?[] values, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }
        return await command.ExecuteScalarAsync(cancellationToken);
    }
}
