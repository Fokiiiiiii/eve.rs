using Eve.Data;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Npgsql;

namespace Eve.Host;

/// <summary>
/// Where the global-config overrides persist. A stored value of <c>null</c> is a deletion.
/// </summary>
public interface IGlobalConfigPersistence
{
    Task<IReadOnlyList<KeyValuePair<string, PyValue?>>> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(string key, PyValue? value, CancellationToken cancellationToken);
}

/// <summary>The overrides of a host without PostgreSQL: they last as long as the process.</summary>
public sealed class InMemoryGlobalConfigPersistence : IGlobalConfigPersistence
{
    private readonly Dictionary<string, PyValue?> _rows = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public Task<IReadOnlyList<KeyValuePair<string, PyValue?>>> LoadAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<KeyValuePair<string, PyValue?>>>([.. _rows]);
        }
    }

    public Task SaveAsync(string key, PyValue? value, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _rows[key] = value;
        }
        return Task.CompletedTask;
    }
}

/// <summary>The overrides in PostgreSQL, in the host's own schema (<c>zsystem</c> unless told otherwise, as the reference's <c>zsystem.GlobalConfig</c>).</summary>
public sealed class PgGlobalConfigPersistence(NpgsqlDataSource source, string schema = PgGlobalConfigPersistence.DefaultSchema) : IGlobalConfigPersistence
{
    public const string DefaultSchema = "zsystem";

    private readonly string _load = $"SELECT key, value FROM {schema}.global_config";

    private readonly string _save =
        $"INSERT INTO {schema}.global_config (key, value, updated_at) VALUES ($1, $2, now()) " +
        "ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = EXCLUDED.updated_at";

    /// <summary>Creates the schema and brings it up to date.</summary>
    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, Migrations.Embedded(typeof(PgGlobalConfigPersistence).Assembly, "Migrations"), cancellationToken);

    public async Task<IReadOnlyList<KeyValuePair<string, PyValue?>>> LoadAsync(CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(_load, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<KeyValuePair<string, PyValue?>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var encoded = reader.IsDBNull(1) ? null : (byte[])reader[1];
            rows.Add(new(reader.GetString(0), encoded is null ? null : MarshalCodec.DecodeExact(encoded)));
        }
        return rows;
    }

    public async Task SaveAsync(string key, PyValue? value, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(_save, connection)
        {
            Parameters =
            {
                new() { Value = key },
                new() { Value = value is null ? (object)DBNull.Value : MarshalCodec.Encode(value) },
            },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>
/// The cluster's global configuration: the settings of <see cref="ClusterOptions"/>, with the values an administrator sets
/// through <c>machoNet.SetGlobalConfigValue</c> on top. What the client reads (<c>GetGlobalConfig</c>, <c>GetInitVals</c>, the
/// handshake's <c>config_vals</c>) is <see cref="Current"/>. A write is saved before it takes effect, so it survives a restart.
/// </summary>
public sealed class GlobalConfigStore
{
    private readonly PyDict _base;
    private readonly IGlobalConfigPersistence _persistence;
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, PyValue?> _overrides = new(StringComparer.Ordinal); // null: deleted

    private GlobalConfigStore(PyDict baseEntries, IGlobalConfigPersistence persistence)
    {
        _base = baseEntries;
        _persistence = persistence;
    }

    /// <summary>Opens the store with the saved overrides applied.</summary>
    public static async Task<GlobalConfigStore> OpenAsync(PyDict baseEntries, IGlobalConfigPersistence persistence, CancellationToken cancellationToken = default)
    {
        var store = new GlobalConfigStore(baseEntries, persistence);
        foreach (var (key, value) in await persistence.LoadAsync(cancellationToken))
        {
            store._overrides[key] = value;
        }
        return store;
    }

    /// <summary>
    /// The configuration the client is given: the base entries in their order with the overrides applied (a deleted key is
    /// gone, a new key comes after the base entries, in name order).
    /// </summary>
    public PyDict Current()
    {
        lock (_gate)
        {
            List<KeyValuePair<PyValue, PyValue>> entries = [];
            HashSet<string> applied = new(StringComparer.Ordinal);
            foreach (var entry in _base.Entries)
            {
                var name = JsSemantics.StringValue(entry.Key) ?? "";
                if (!_overrides.TryGetValue(name, out var value))
                {
                    entries.Add(entry);
                    continue;
                }
                applied.Add(name);
                if (value is not null)
                {
                    entries.Add(new(entry.Key, value));
                }
            }
            foreach (var (name, value) in _overrides.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (value is not null && !applied.Contains(name))
                {
                    entries.Add(new(new PyStr(name), value));
                }
            }
            return new PyDict(entries);
        }
    }

    /// <summary>
    /// Saves a write and makes it current. A value of None or a delete removes the key (the client removes it too).
    /// Returns the value now set, or null for a deletion. A save that fails changes nothing.
    /// </summary>
    public async ValueTask<PyValue?> ApplyAsync(string key, PyValue value, bool isDelete, CancellationToken cancellationToken = default)
    {
        var stored = isDelete || value is PyNone ? null : value;
        await _writes.WaitAsync(cancellationToken);
        try
        {
            await _persistence.SaveAsync(key, stored, cancellationToken);
            lock (_gate)
            {
                _overrides[key] = stored;
            }
        }
        finally
        {
            _writes.Release();
        }
        return stored;
    }
}

/// <summary>
/// The write path of <c>machoNet.SetGlobalConfigValue</c> in a host: the write is saved in the store, the connected clients are
/// told (when <c>isUpdateClients</c>, as the reference's <c>Broadcast</c>: a removal is sent as None), and the fronts are given
/// the new configuration (<paramref name="clusterConfigChanged"/>), so the next handshake reads it.
/// </summary>
public static class GlobalConfigWrites
{
    /// <summary>
    /// Keys no administrator may set or remove: the bug-report server (a value elsewhere would send reports off this machine)
    /// and the Honeycomb write key (EVE.js <c>globalConfig.js</c> protects the same two). A write to one is refused with an error.
    /// </summary>
    public static readonly IReadOnlySet<string> ProtectedKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "bugReporting_BugReportServer",
        "HoneycombWriteKey",
    };

    public static MachoNetGlobalConfigUpdateCallback Create(GlobalConfigStore config, ConnectedSessions connected, Action clusterConfigChanged) =>
        async (update, _, cancellationToken) =>
        {
            if (ProtectedKeys.Contains(update.Key))
            {
                throw new MachoWrappedException(MachoErrors.Wrapped("exceptions.RuntimeError", [
                    new PyStr("ProtectedGlobalConfigKey"),
                    new PyStr($"{update.Key} is not set through machoNet.SetGlobalConfigValue"),
                ]));
            }
            var stored = await config.ApplyAsync(update.Key, update.Value, update.IsDelete, cancellationToken);
            if (update.IsUpdateClients)
            {
                foreach (var session in connected.Snapshot())
                {
                    session.SendNotification("OnGlobalConfigUpdated", "clientID", new PyStr(update.Key), stored ?? PyNone.Instance);
                }
            }
            // A single node: there is no other node to tell (isUpdate).
            clusterConfigChanged();
        };
}
