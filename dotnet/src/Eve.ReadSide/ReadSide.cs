using System.Collections.Concurrent;
using System.Text;
using Eve.Data;
using Eve.Marshal;
using Eve.Services;
using Npgsql;

namespace Eve.ReadSide;

/// <summary>The rows a query returned, as plain values in column order.</summary>
public sealed class RowSet
{
    internal RowSet(RowsetQuery query, IReadOnlyList<object?[]> rows)
    {
        Query = query;
        Rows = rows;
    }

    public RowsetQuery Query { get; }
    public IReadOnlyList<Column> Columns => Query.Columns;
    public IReadOnlyList<object?[]> Rows { get; }
    public int Count => Rows.Count;

    private static PyValue Value(ColumnType type, object? value) => value is null ? PyValue.None : type switch
    {
        ColumnType.Int => new PyInt(Convert.ToInt64(value)),
        ColumnType.Long or ColumnType.FileTime => new PyLong(Convert.ToInt64(value)),
        ColumnType.Real => new PyReal(Convert.ToDouble(value)),
        ColumnType.Text => new PyWString((string)value),
        ColumnType.Bool => (bool)value ? PyBool.True : PyBool.False,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>The values of a row as the client's types (null becomes None).</summary>
    public PyValue[] Values(object?[] row) => [.. Columns.Select((c, i) => Value(c.Type, row[i]))];

    /// <summary>A <c>Rowset</c> (<c>eve.common.script.sys.rowset.Rowset</c>): the column names and a list per row. What most list-returning service calls return.</summary>
    public PyValue ToRowset() => Py.Rowset([.. Columns.Select(c => c.Name)], Rows.Select(row => (PyValue)new PyList(Values(row))));

    /// <summary>A database rowset (<c>carbon.common.script.sys.crowset.CRowset</c>) with a shared descriptor.</summary>
    public PyValue ToCRowset()
    {
        var descriptor = Descriptor();
        return new PyObject(new PyStr("carbon.common.script.sys.crowset.CRowset"), Py.Dict(
            ("header", descriptor.Header),
            ("RowClass", new PyToken("util.Row")),
            ("lines", new PyList(ToPackedRows()))));
    }

    private Descriptor Descriptor() => Eve.Marshal.Descriptor.FromHeader(new PyObjectEx(ObjectExKind.Ex1,
        Py.Tuple(new PyToken("blue.DBRowDescriptor"), Py.Tuple(Py.Tuple([.. Columns.Select(c => (PyValue)Py.Tuple(new PyStr(c.Name), new PyInt(c.Type.DbType())))]))), [], []));

    /// <summary>One packed row (<c>blue.DBRowDescriptor</c>) per row, over a shared descriptor: what <c>dbutil.CRowset</c>-style calls return.</summary>
    public IReadOnlyList<PyValue> ToPackedRows()
    {
        var descriptor = Descriptor();
        return [.. Rows.Select(row => (PyValue)new PyPackedRow(descriptor, Values(row)))];
    }

    /// <summary>A <c>CIndexedRowset</c>: packed rows by the value of the key column (the query's, or the first column).</summary>
    public PyValue ToIndexedRowset()
    {
        var keyName = Query.KeyColumn ?? Columns[0].Name;
        var key = Columns.ToList().FindIndex(c => c.Name == keyName);
        if (key < 0)
        {
            throw new InvalidOperationException($"query {Query.Name} has no column {keyName}");
        }
        var descriptor = Descriptor();
        return new PyObjectEx(ObjectExKind.Ex2,
            Py.Tuple(Py.Tuple(new PyToken("carbon.common.script.sys.crowset.CIndexedRowset")),
                Py.Dict(("header", descriptor.Header), ("idName", new PyStr(keyName)), ("columnName", new PyStr(keyName)))), [],
            [.. Rows.Select(row => new KeyValuePair<PyValue, PyValue>(Value(Columns[key].Type, row[key]), new PyPackedRow(descriptor, Values(row))))]);
    }

    /// <summary>A dictionary from the key column to a <c>util.KeyVal</c> of the row (what <c>GetXxxInfo</c> calls that return a record per id return).</summary>
    public PyValue ToKeyValsByKey()
    {
        var keyName = Query.KeyColumn ?? Columns[0].Name;
        var key = Columns.ToList().FindIndex(c => c.Name == keyName);
        return new PyDict([.. Rows.Select(row => new KeyValuePair<PyValue, PyValue>(Value(Columns[key].Type, row[key]),
            Py.KeyVal([.. Columns.Select((c, i) => (c.Name, Value(c.Type, row[i])))])))]);
    }

    /// <summary>The first row as a <c>util.KeyVal</c>, or None when there is none.</summary>
    public PyValue ToKeyVal() => Rows.Count == 0 ? PyValue.None : Py.KeyVal([.. Columns.Select((c, i) => (c.Name, Value(c.Type, Rows[0][i])))]);
}

/// <summary>
/// The server's read side: named queries over the static data and over the tables of the modules, answered as the rowsets the client expects. A feature that needs "the types of a
/// group", "the stations of a system" or "my open orders" registers a <see cref="RowsetQuery"/> and returns <c>RowsAsync(name, args).ToRowset()</c> (or the shape the call needs). The
/// SQL stays in one place, the shapes are built once and tested, and the static data is cached. See <c>docs/FOUNDATIONS.md</c>.
/// </summary>
public sealed class ReadSide
{
    private readonly NpgsqlDataSource _source;
    private readonly Dictionary<string, string> _schemas;
    private readonly Dictionary<string, RowsetQuery> _queries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RowSet> _cache = new(StringComparer.Ordinal);

    /// <param name="schemas">The schemas queries name in braces: <c>{sde}</c> by default; add <c>["market"] = "market"</c> to write <c>{market}.orders</c>.</param>
    public ReadSide(NpgsqlDataSource source, IReadOnlyDictionary<string, string>? schemas = null)
    {
        _source = source;
        _schemas = new Dictionary<string, string>(StringComparer.Ordinal) { ["sde"] = Eve.Sde.SdeReader.DefaultSchema };
        foreach (var (name, schema) in schemas ?? new Dictionary<string, string>())
        {
            _schemas[name] = Sql.CheckSchema(schema);
        }
    }

    /// <summary>Adds a query. The SQL is built once; a query that names a column or schema that is not there fails at the first use.</summary>
    public ReadSide Register(RowsetQuery query)
    {
        if (query.Columns.Count == 0 || query.Columns.Select(c => c.Name).Distinct().Count() != query.Columns.Count)
        {
            throw new ArgumentException($"query {query.Name} needs distinct columns", nameof(query));
        }
        if (query.Columns.Any(c => !ColumnTypes.Word().IsMatch(c.Name)))
        {
            throw new ArgumentException($"query {query.Name} has a column name that is not a word", nameof(query));
        }
        if (!_queries.TryAdd(query.Name, query))
        {
            throw new InvalidOperationException($"a query named '{query.Name}' is already registered");
        }
        return this;
    }

    public IReadOnlyCollection<string> Names => _queries.Keys;

    /// <summary>The SQL a query runs (for tests and for reading what a registered query does).</summary>
    public string SqlOf(string name)
    {
        var query = _queries.TryGetValue(name, out var found) ? found : throw new KeyNotFoundException($"no query named '{name}'");
        var text = new StringBuilder("SELECT ");
        text.Append(string.Join(", ", query.Columns.Select(c => $"({c.Sql})::{c.Type.SqlType()} AS {c.Name}")));
        text.Append(" FROM ").Append(Resolve(query.From));
        if (query.Where is not null)
        {
            text.Append(" WHERE ").Append(query.Where);
        }
        if (query.OrderBy is not null)
        {
            text.Append(" ORDER BY ").Append(query.OrderBy);
        }
        if (query.Limit is { } limit)
        {
            text.Append(" LIMIT ").Append(limit);
        }
        return text.ToString();
    }

    private string Resolve(string from)
    {
        var result = from;
        foreach (var (name, schema) in _schemas)
        {
            result = result.Replace("{" + name + "}", schema);
        }
        if (result.Contains('{'))
        {
            throw new InvalidOperationException($"the query names a schema the read side was not given: {from}");
        }
        return result;
    }

    /// <summary>Runs a query with the values of the request (<c>$1</c>, <c>$2</c>…).</summary>
    public async Task<RowSet> RowsAsync(string name, object?[]? args = null, CancellationToken cancellationToken = default)
    {
        args ??= [];
        var query = _queries.TryGetValue(name, out var found) ? found : throw new KeyNotFoundException($"no query named '{name}'");
        var cacheKey = query.Cache ? name + "|" + string.Join("|", args.Select(a => a?.ToString())) : null;
        if (cacheKey is not null && _cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(connection, null, SqlOf(name), args);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<object?[]>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new object?[query.Columns.Count];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }
        var result = new RowSet(query, rows);
        if (cacheKey is not null)
        {
            _cache[cacheKey] = result;
        }
        return result;
    }
}
