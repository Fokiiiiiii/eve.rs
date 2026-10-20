using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Eve.GameStore;

public sealed partial class PersistenceStore
{
    private async Task CreateTableAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string table, CancellationToken ct)
    {
        if (_ensuredTables.ContainsKey(table))
        {
            return;
        }
        // Concurrent CREATE TABLE IF NOT EXISTS of the same name can still fail on
        // a catalog unique index, so creation is serialised per table.
        await LockAsync(connection, tx, $"table:{table}", ct);
        await ExecuteAsync(
            connection, tx,
            $"CREATE TABLE IF NOT EXISTS {Qualified(table)} (key text PRIMARY KEY, doc jsonb NOT NULL, rev bigint NOT NULL DEFAULT 1)",
            ct);
    }

    /// <summary>
    /// Upserts (later entries win for a repeated key), then deletes. A row whose
    /// document did not change keeps its revision. Returns upserts + deletes, as
    /// the reference does.
    /// </summary>
    private async Task<int> ApplyChangesCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        string table,
        IReadOnlyList<RowUpsert> upserts,
        IReadOnlyList<string> deletes,
        CancellationToken ct)
    {
        if (upserts.Count > 0)
        {
            var latest = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var upsert in upserts)
            {
                latest[upsert.Key] = upsert.Json;
            }
            await ExecuteAsync(
                connection, tx,
                $"""
                INSERT INTO {Qualified(table)} AS t (key, doc)
                SELECT k, d FROM unnest(@keys, @docs) AS u (k, d)
                ON CONFLICT (key) DO UPDATE SET doc = EXCLUDED.doc, rev = t.rev + 1
                WHERE t.doc IS DISTINCT FROM EXCLUDED.doc
                """,
                ct,
                ("keys", NpgsqlDbType.Array | NpgsqlDbType.Text, latest.Keys.ToArray()),
                ("docs", NpgsqlDbType.Array | NpgsqlDbType.Jsonb, latest.Values.ToArray()));
        }
        if (deletes.Count > 0)
        {
            await ExecuteAsync(
                connection, tx,
                $"DELETE FROM {Qualified(table)} WHERE key = ANY(@keys)",
                ct, ("keys", NpgsqlDbType.Array | NpgsqlDbType.Text, deletes.Distinct().ToArray()));
        }
        return upserts.Count + deletes.Count;
    }

    /// <summary>
    /// A direct, fenced batch that bypasses the journal (imports, maintenance).
    /// Regular flushes go through <see cref="EnqueueAsync"/> instead.
    /// </summary>
    public async Task<int> ApplyChangesAsync(
        string table,
        IEnumerable<RowUpsert>? upserts,
        IEnumerable<string>? deletes,
        OwnerFence fence,
        CancellationToken ct = default)
    {
        var name = TableNames.RequirePersistence(table);
        var validatedUpserts = ValidateUpserts(upserts);
        var validatedDeletes = ValidateDeletes(deletes);
        if (validatedUpserts.Count == 0 && validatedDeletes.Count == 0)
        {
            return 0;
        }
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await AssertActiveAsync(connection, tx, fence, ct);
        await CreateTableAsync(connection, tx, name, ct);
        var changed = await ApplyChangesCoreAsync(connection, tx, name, validatedUpserts, validatedDeletes, ct);
        await tx.CommitAsync(ct);
        _ensuredTables.TryAdd(name, 0);
        return changed;
    }

    public async Task<int> ApplyChangesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string table,
        IEnumerable<RowUpsert>? upserts,
        IEnumerable<string>? deletes,
        OwnerFence fence,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        var name = TableNames.RequirePersistence(table);
        var validatedUpserts = ValidateUpserts(upserts);
        var validatedDeletes = ValidateDeletes(deletes);
        if (validatedUpserts.Count == 0 && validatedDeletes.Count == 0)
            return 0;

        await AssertActiveAsync(connection, transaction, fence, ct);
        await CreateTableAsync(connection, transaction, name, ct);
        return await ApplyChangesCoreAsync(connection, transaction, name, validatedUpserts, validatedDeletes, ct);
    }

    /// <summary>
    /// Replaces the whole table with <paramref name="table"/>'s rows, using COPY.
    /// Returns the number of stored rows.
    /// </summary>
    public async Task<int> ReplaceAllAsync(string tableName, JsonObject? table, OwnerFence fence, CancellationToken ct = default)
    {
        var name = TableNames.RequirePersistence(tableName);
        var rows = RowCodec.Explode(name, table);
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await AssertActiveAsync(connection, tx, fence, ct);
        await CreateTableAsync(connection, tx, name, ct);
        await ExecuteAsync(connection, tx, $"DELETE FROM {Qualified(name)}", ct);
        if (rows.Count > 0)
        {
            await RunAsync(async () =>
            {
                await using var importer = await connection.BeginBinaryImportAsync($"COPY {Qualified(name)} (key, doc) FROM STDIN (FORMAT BINARY)", ct);
                foreach (var (key, value) in rows)
                {
                    await importer.StartRowAsync(ct);
                    await importer.WriteAsync(key, NpgsqlDbType.Text, ct);
                    await importer.WriteAsync(value is null ? "null" : value.ToJsonString(), NpgsqlDbType.Jsonb, ct);
                }
                return await importer.CompleteAsync(ct);
            });
        }
        await tx.CommitAsync(ct);
        _ensuredTables.TryAdd(name, 0);
        return rows.Count;
    }

    /// <summary>
    /// Replaces the whole table with already-stored rows (row keys as they are in
    /// the table, group separators included), streaming them through COPY. For
    /// imports. Returns the number of rows written.
    /// </summary>
    public async Task<int> ReplaceRowsAsync(string tableName, IEnumerable<StoredRow> rows, OwnerFence fence, CancellationToken ct = default)
    {
        var name = TableNames.RequirePersistence(tableName);
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await AssertActiveAsync(connection, tx, fence, ct);
        await CreateTableAsync(connection, tx, name, ct);
        await ExecuteAsync(connection, tx, $"DELETE FROM {Qualified(name)}", ct);
        var count = 0;
        await RunAsync(async () =>
        {
            await using var importer = await connection.BeginBinaryImportAsync($"COPY {Qualified(name)} (key, doc) FROM STDIN (FORMAT BINARY)", ct);
            foreach (var row in rows)
            {
                await importer.StartRowAsync(ct);
                await importer.WriteAsync(row.Key, NpgsqlDbType.Text, ct);
                await importer.WriteAsync(row.Json, NpgsqlDbType.Jsonb, ct);
                count += 1;
            }
            return await importer.CompleteAsync(ct);
        });
        await tx.CommitAsync(ct);
        _ensuredTables.TryAdd(name, 0);
        return count;
    }

    public async Task<bool> TableExistsAsync(string table, CancellationToken ct = default)
    {
        var name = TableNames.RequirePersistence(table);
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var command = Command(
            connection, null, "SELECT to_regclass(@name) IS NOT NULL",
            ("name", NpgsqlDbType.Text, Qualified(name)));
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// The stored rows of a table. A missing table is created empty for a writer
    /// (<paramref name="createIfMissing"/>) and an error for a passive reader,
    /// where an empty answer would look like a legitimately empty table.
    /// </summary>
    public async Task<IReadOnlyList<StoredRow>> LoadRowsAsync(string table, bool createIfMissing = true, CancellationToken ct = default)
    {
        var name = TableNames.RequirePersistence(table);
        if (createIfMissing)
        {
            await EnsureTableAsync(name, ct);
        }
        else if (!createIfMissing && !await TableExistsAsync(name, ct))
        {
            throw new PersistenceException($"table does not exist: {name}", PersistenceCodes.TableNotFound, new Dictionary<string, object?> { ["table"] = name });
        }
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var command = Command(connection, null, $"SELECT key, doc::text FROM {Qualified(name)}");
        var rows = new List<StoredRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new StoredRow(reader.GetString(0), reader.GetString(1)));
        }
        return rows;
    }

    /// <summary>The whole table as one object, with row-group tables reassembled.</summary>
    public async Task<JsonObject> LoadTableAsync(string table, bool createIfMissing = true, CancellationToken ct = default)
    {
        var rows = await LoadRowsAsync(table, createIfMissing, ct);
        return RowCodec.Assemble(table, rows.Select(row => new KeyValuePair<string, JsonNode?>(row.Key, JsonNode.Parse(row.Json))));
    }

    /// <summary>
    /// One stored row, read in the caller's transaction (a subscriber reads the row it is about to write, in the same transaction). The table is created
    /// when missing, as a writer does. Null when the row is not there.
    /// </summary>
    public async Task<string?> ReadRowAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string table, string key, CancellationToken ct = default)
    {
        var name = TableNames.RequirePersistence(table);
        await CreateTableAsync(connection, transaction, name, ct);
        await using var command = Command(connection, transaction, $"SELECT doc::text FROM {Qualified(name)} WHERE key = @key", ("key", NpgsqlDbType.Text, key));
        return (string?)await command.ExecuteScalarAsync(ct);
    }

    public async Task<long> RowCountAsync(string table, CancellationToken ct = default)
    {
        var name = TableNames.RequirePersistence(table);
        await EnsureTableAsync(name, ct);
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var command = Command(connection, null, $"SELECT count(*) FROM {Qualified(name)}");
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    private async Task EnsureTableAsync(string name, CancellationToken ct)
    {
        if (_ensuredTables.ContainsKey(name))
        {
            return;
        }
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await CreateTableAsync(connection, tx, name, ct);
        await tx.CommitAsync(ct);
        _ensuredTables.TryAdd(name, 0);
    }

    public async Task<bool> IsMigratedAsync(string table, CancellationToken ct = default)
    {
        var name = TableNames.RequireSafe(table);
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var command = Command(
            connection, null, $"SELECT EXISTS (SELECT 1 FROM {Qualified(TableNames.Migrations)} WHERE table_name = @name)",
            ("name", NpgsqlDbType.Text, name));
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Records that a table was imported from a legacy source, so a later empty table is never re-seeded.</summary>
    public async Task MarkMigratedAsync(string table, OwnerFence fence, CancellationToken ct = default)
    {
        var name = TableNames.RequireSafe(table);
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await AssertActiveAsync(connection, tx, fence, ct);
        await CreateTableAsync(connection, tx, name, ct);
        await ExecuteAsync(
            connection, tx,
            $"INSERT INTO {Qualified(TableNames.Migrations)} (table_name, migrated_at) VALUES (@name, now()) ON CONFLICT (table_name) DO NOTHING",
            ct, ("name", NpgsqlDbType.Text, name));
        await tx.CommitAsync(ct);
        _ensuredTables.TryAdd(name, 0);
    }
}
