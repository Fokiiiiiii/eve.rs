using Microsoft.Data.Sqlite;

namespace Eve.GameStore.Import;

/// <summary>A pending or applied batch in the source's journal.</summary>
internal sealed record SourceOperation(long OperationId, string Table, string State, IReadOnlyList<RowUpsert> Upserts, IReadOnlyList<string> Deletes);

internal sealed record SourceOwner(string Role, long Epoch, string InstanceId, bool Active, long LeaseExpiresAt);

/// <summary>Reads a gamestore.sqlite written by the reference server.</summary>
internal sealed class SqliteSource : IDisposable
{
    private static readonly string[] InternalTables = [TableNames.Migrations, TableNames.Outbox, TableNames.Owners];

    private readonly SqliteConnection _connection;

    public SqliteSource(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"SQLite database not found: {path}", path);
        // A one-shot import must release its file handle when disposed, including on Windows.
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ConnectionString);
        _connection.Open();
    }

    private SqliteCommand Command(string sql, params (string Name, object Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return command;
    }

    private bool Exists(string table)
    {
        using var command = Command("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name", ("$name", table));
        return command.ExecuteScalar() is not null;
    }

    private IReadOnlyList<string> Columns(string table)
    {
        using var command = Command($"PRAGMA table_info(\"{table}\")");
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }
        return columns;
    }

    /// <summary>The key/json tables that hold game state, and the tables that are skipped and why.</summary>
    public (IReadOnlyList<string> Tables, IReadOnlyList<(string Table, string Reason)> Skipped) ListTables()
    {
        var tables = new List<string>();
        var skipped = new List<(string, string)>();
        using var command = Command("SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name");
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }
        foreach (var name in names)
        {
            if (name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase) || InternalTables.Contains(name))
            {
                continue;
            }
            if (!TableNames.IsSafe(name))
            {
                skipped.Add((name, "not a plain table name"));
                continue;
            }
            var columns = Columns(name);
            if (!columns.Contains("key") || !columns.Contains("json"))
            {
                skipped.Add((name, "not a key/json table"));
                continue;
            }
            tables.Add(name);
        }
        return (tables, skipped);
    }

    public IEnumerable<StoredRow> Rows(string table)
    {
        using var command = Command($"SELECT key, json FROM \"{TableNames.RequireSafe(table)}\"");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return new StoredRow(reader.GetString(0), reader.GetString(1));
        }
    }

    public long RowCount(string table)
    {
        using var command = Command($"SELECT count(*) FROM \"{TableNames.RequireSafe(table)}\"");
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<string> Migrated()
    {
        if (!Exists(TableNames.Migrations))
        {
            return [];
        }
        using var command = Command($"SELECT table_name FROM \"{TableNames.Migrations}\" ORDER BY table_name");
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    public IReadOnlyList<SourceOwner> Owners()
    {
        if (!Exists(TableNames.Owners))
        {
            return [];
        }
        using var command = Command($"SELECT owner_role, epoch, instance_id, active, lease_expires_at FROM \"{TableNames.Owners}\"");
        using var reader = command.ExecuteReader();
        var result = new List<SourceOwner>();
        while (reader.Read())
        {
            result.Add(new SourceOwner(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3) == 1, reader.GetInt64(4)));
        }
        return result;
    }

    public IReadOnlyList<SourceOperation> Journal()
    {
        if (!Exists(TableNames.Outbox))
        {
            return [];
        }
        using var command = Command($"SELECT operation_id, table_name, state, upserts_json, deletes_json FROM \"{TableNames.Outbox}\" ORDER BY operation_id");
        using var reader = command.ExecuteReader();
        var result = new List<SourceOperation>();
        while (reader.Read())
        {
            var upserts = new List<RowUpsert>();
            using (var document = System.Text.Json.JsonDocument.Parse(reader.GetString(3)))
            {
                foreach (var pair in document.RootElement.EnumerateArray())
                {
                    upserts.Add(new RowUpsert(pair[0].GetString()!, pair[1].GetString()!));
                }
            }
            using var deletes = System.Text.Json.JsonDocument.Parse(reader.GetString(4));
            result.Add(new SourceOperation(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), upserts,
                deletes.RootElement.EnumerateArray().Select(e => e.GetString()!).ToList()));
        }
        return result;
    }

    public void Dispose() => _connection.Dispose();
}
