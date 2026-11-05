using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace Eve.Sde;

/// <summary>One record of an SDE table: its key and its document.</summary>
public sealed record SdeRow(long Key, JsonElement Doc);

/// <summary>What a choice among all types needs of each: its group, its English name and whether it is published.</summary>
public sealed record SdeTypeSummary(int TypeId, int GroupId, string Name, bool Published);

/// <summary>
/// Read access to a static data export as <c>eve-sde</c> loads it: one table per file in the selected schema,
/// <c>(ord, key, doc jsonb)</c>. The default schema is the client-pinned <c>sde</c> export.
/// </summary>
public sealed partial class SdeReader
{
    public const string DefaultSchema = "sde";
    public const string MissionSchema = "sde_mission";

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex SchemaName();

    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex TableName();

    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<NpgsqlDataSource, SdeBuildCache> SdeBuildCaches = new();
    private readonly SdeBuildCache _sdeBuildCache;
    private long _buildNumber = -1;

    private sealed class SdeBuildCache
    {
        private readonly object _gate = new();
        private readonly Dictionary<(string Schema, long Build, string Table), Lazy<Task<IReadOnlyList<SdeRow>>>> _tables = [];
        private readonly Dictionary<(string Schema, long Build), Lazy<Task<IReadOnlyList<SdeTypeSummary>>>> _summaries = [];

        public async Task<IReadOnlyList<SdeRow>> ReadAsync(
            string schema,
            long build,
            string table,
            Func<Task<IReadOnlyList<SdeRow>>> load,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (schema, build, table);
            Lazy<Task<IReadOnlyList<SdeRow>>> lazy;
            lock (_gate)
            {
                if (!_tables.TryGetValue(key, out lazy!))
                {
                    lazy = new Lazy<Task<IReadOnlyList<SdeRow>>>(load, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);
                    _tables.Add(key, lazy);
                }
            }

            var task = lazy.Value;
            try
            {
                return await task.WaitAsync(cancellationToken);
            }
            catch
            {
                if (task.IsFaulted || task.IsCanceled)
                {
                    lock (_gate)
                    {
                        if (_tables.TryGetValue(key, out var current) && ReferenceEquals(current, lazy))
                        {
                            _tables.Remove(key);
                        }
                    }
                }
                throw;
            }
        }

        public async Task<IReadOnlyList<SdeTypeSummary>> ReadSummariesAsync(
            string schema,
            long build,
            Func<Task<IReadOnlyList<SdeTypeSummary>>> load,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (schema, build);
            Lazy<Task<IReadOnlyList<SdeTypeSummary>>> lazy;
            lock (_gate)
            {
                if (!_summaries.TryGetValue(key, out lazy!))
                {
                    lazy = new Lazy<Task<IReadOnlyList<SdeTypeSummary>>>(load, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);
                    _summaries.Add(key, lazy);
                }
            }

            var task = lazy.Value;
            try
            {
                return await task.WaitAsync(cancellationToken);
            }
            catch
            {
                if (task.IsFaulted || task.IsCanceled)
                {
                    lock (_gate)
                    {
                        if (_summaries.TryGetValue(key, out var current) && ReferenceEquals(current, lazy))
                        {
                            _summaries.Remove(key);
                        }
                    }
                }
                throw;
            }
        }

        public void Invalidate(string schema, long build)
        {
            lock (_gate)
            {
                foreach (var key in _tables.Keys.Where(key => key.Schema == schema && key.Build != build).ToArray())
                {
                    _tables.Remove(key);
                }
                foreach (var key in _summaries.Keys.Where(key => key.Schema == schema && key.Build != build).ToArray())
                {
                    _summaries.Remove(key);
                }
            }
        }
    }

    public SdeReader(NpgsqlDataSource source, string schema = DefaultSchema)
    {
        if (!SchemaName().IsMatch(schema))
        {
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        }
        _source = source;
        _schema = schema;
        _sdeBuildCache = SdeBuildCaches.GetValue(source, static _ => new SdeBuildCache());
    }

    /// <summary>The build number of the loaded export, or null when none is loaded.</summary>
    public async Task<long?> BuildAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var exists = new NpgsqlCommand("SELECT to_regclass($1) IS NOT NULL", connection) { Parameters = { new() { Value = $"{_schema}._build" } } };
        if (!(bool)(await exists.ExecuteScalarAsync(cancellationToken))!)
        {
            Interlocked.Exchange(ref _buildNumber, -1);
            return null;
        }
        await using var read = new NpgsqlCommand($"SELECT build_number FROM {_schema}._build", connection);
        var build = await read.ExecuteScalarAsync(cancellationToken) as long?;
        if (build is long buildNumber)
        {
            _sdeBuildCache.Invalidate(_schema, buildNumber);
            Interlocked.Exchange(ref _buildNumber, buildNumber);
        }
        else
        {
            Interlocked.Exchange(ref _buildNumber, -1);
        }

        return build;
    }

    /// <summary>
    /// The records of <paramref name="table"/> in file order, all of them or only those whose key is in
    /// <paramref name="keys"/>. Records whose key is not a number are skipped.
    /// </summary>
    public async Task<IReadOnlyList<SdeRow>> ReadAsync(string table, IEnumerable<long>? keys = null, CancellationToken cancellationToken = default)
    {
        if (!TableName().IsMatch(table))
        {
            throw new ArgumentException($"not a usable table name: {table}", nameof(table));
        }

        var isHotFullTable = table is "types" or "typeDogma" or "missions" or "mapSolarSystems";
        if (keys is null && isHotFullTable)
        {
            var build = Interlocked.Read(ref _buildNumber);
            if (build >= 0)
            {
                return await _sdeBuildCache.ReadAsync(
                    _schema,
                    build,
                    table,
                    () => ReadCoreAsync(table, null, CancellationToken.None),
                    cancellationToken);
            }
        }

        return await ReadCoreAsync(table, keys, cancellationToken);
    }

    private async Task<IReadOnlyList<SdeRow>> ReadCoreAsync(string table, IEnumerable<long>? keys, CancellationToken cancellationToken)
    {
        var rows = new List<SdeRow>();
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            keys is null
                ? $"SELECT key, doc FROM {_schema}.\"{table}\" ORDER BY ord"
                : $"SELECT key, doc FROM {_schema}.\"{table}\" WHERE key = ANY($1) ORDER BY ord",
            connection);
        if (keys is not null)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = keys.Select(k => k.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray() });
        }
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (long.TryParse(reader.GetString(0), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var key))
            {
                rows.Add(new SdeRow(key, reader.GetFieldValue<JsonDocument>(1).RootElement.Clone()));
            }
        }
        return keys is null ? rows.AsReadOnly() : rows;
    }

    /// <summary>
    /// The records of <paramref name="table"/> whose number field <paramref name="field"/> is one of <paramref name="values"/>
    /// (<c>groupID</c> of the types of some groups), in file order.
    /// </summary>
    public async Task<IReadOnlyList<SdeRow>> ReadByFieldAsync(string table, string field, IEnumerable<long> values, CancellationToken cancellationToken = default)
    {
        if (!TableName().IsMatch(table) || !TableName().IsMatch(field))
        {
            throw new ArgumentException($"not a usable table or field name: {table}.{field}");
        }
        var rows = new List<SdeRow>();
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT key, doc FROM {_schema}.\"{table}\" WHERE jsonb_typeof(doc->'{field}') = 'number' AND (doc->>'{field}')::bigint = ANY($1) ORDER BY ord",
            connection)
        {
            Parameters = { new() { Value = values.ToArray() } },
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (long.TryParse(reader.GetString(0), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var key))
            {
                rows.Add(new SdeRow(key, reader.GetFieldValue<JsonDocument>(1).RootElement.Clone()));
            }
        }
        return rows;
    }

    /// <summary>
    /// The records of <paramref name="table"/> whose document contains <paramref name="containment"/> (a JSON value, matched with PostgreSQL's <c>@&gt;</c>), in file order.
    /// This asks which types carry an effect (<c>{"dogmaEffects":[{"effectID":101}]}</c>) without reading every document.
    /// </summary>
    public async Task<IReadOnlyList<SdeRow>> ReadContainingAsync(string table, string containment, CancellationToken cancellationToken = default)
    {
        if (!TableName().IsMatch(table))
        {
            throw new ArgumentException($"not a usable table name: {table}", nameof(table));
        }
        var rows = new List<SdeRow>();
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT key, doc FROM {_schema}.\"{table}\" WHERE doc @> $1::jsonb ORDER BY ord", connection)
        {
            Parameters = { new() { Value = containment } },
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (long.TryParse(reader.GetString(0), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var key))
            {
                rows.Add(new SdeRow(key, reader.GetFieldValue<JsonDocument>(1).RootElement.Clone()));
            }
        }
        return rows;
    }

    /// <summary>Every type in file order with only what a choice among them needs (the whole documents of the types are large; a type without a published flag is published).</summary>
    private async Task<IReadOnlyList<SdeTypeSummary>> ReadTypeSummariesCoreAsync(CancellationToken cancellationToken)
    {
        var types = new List<SdeTypeSummary>();
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT key, doc->>'groupID', doc->'name'->>'en', doc->>'published' FROM {_schema}.\"types\" ORDER BY ord", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (long.TryParse(reader.GetString(0), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var key)
                && !reader.IsDBNull(1) && int.TryParse(reader.GetString(1), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var group))
            {
                types.Add(new SdeTypeSummary((int)key, group, reader.IsDBNull(2) ? "" : reader.GetString(2), reader.IsDBNull(3) || reader.GetString(3) != "false"));
            }
        }
        return types.AsReadOnly();
    }

    public Task<IReadOnlyList<SdeTypeSummary>> ReadTypeSummariesAsync(CancellationToken cancellationToken = default)
    {
        var build = Interlocked.Read(ref _buildNumber);
        if (build < 0)
        {
            return ReadTypeSummariesCoreAsync(cancellationToken);
        }
        return _sdeBuildCache.ReadSummariesAsync(
            _schema,
            build,
            () => ReadTypeSummariesCoreAsync(CancellationToken.None),
            cancellationToken);
    }

    /// <summary>A text in the export's English, from a <c>{"en": ..., ...}</c> object.</summary>
    public static string English(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty("en", out var en) && en.ValueKind == JsonValueKind.String
            ? en.GetString() ?? ""
            : "";
}
