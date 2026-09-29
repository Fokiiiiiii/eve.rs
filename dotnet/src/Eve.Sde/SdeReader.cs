using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace Eve.Sde;

/// <summary>One record of an SDE table: its key and its document.</summary>
public sealed record SdeRow(long Key, JsonElement Doc);

/// <summary>
/// Read access to the static data export as <c>eve-sde</c> loads it: one table per file in the <c>sde</c>
/// schema, <c>(ord, key, doc jsonb)</c>. The export is loaded once and never written by the server.
/// </summary>
public sealed partial class SdeReader
{
    public const string DefaultSchema = "sde";

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex SchemaName();

    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex TableName();

    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public SdeReader(NpgsqlDataSource source, string schema = DefaultSchema)
    {
        if (!SchemaName().IsMatch(schema))
        {
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        }
        _source = source;
        _schema = schema;
    }

    /// <summary>The build number of the loaded export, or null when none is loaded.</summary>
    public async Task<long?> BuildAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var exists = new NpgsqlCommand("SELECT to_regclass($1) IS NOT NULL", connection) { Parameters = { new() { Value = $"{_schema}._build" } } };
        if (!(bool)(await exists.ExecuteScalarAsync(cancellationToken))!)
        {
            return null;
        }
        await using var read = new NpgsqlCommand($"SELECT build_number FROM {_schema}._build", connection);
        return await read.ExecuteScalarAsync(cancellationToken) as long?;
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
        return rows;
    }

    /// <summary>A text in the export's English, from a <c>{"en": ..., ...}</c> object.</summary>
    public static string English(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty("en", out var en) && en.ValueKind == JsonValueKind.String
            ? en.GetString() ?? ""
            : "";
}
