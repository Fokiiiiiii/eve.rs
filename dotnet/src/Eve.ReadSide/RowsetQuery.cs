using System.Text.RegularExpressions;

namespace Eve.ReadSide;

/// <summary>The kinds of value a column holds, with the client's DBTYPE code for each (what a packed row declares).</summary>
public enum ColumnType
{
    /// <summary>A 32-bit integer (DBTYPE_I4, 3).</summary>
    Int,

    /// <summary>A 64-bit integer (DBTYPE_I8, 20).</summary>
    Long,

    /// <summary>A real number (DBTYPE_R8, 5).</summary>
    Real,

    /// <summary>Text (DBTYPE_WSTR, 130).</summary>
    Text,

    /// <summary>true or false (DBTYPE_BOOL, 11).</summary>
    Bool,

    /// <summary>A Windows FILETIME kept as a 64-bit integer (DBTYPE_FILETIME, 64).</summary>
    FileTime,
}

public static partial class ColumnTypes
{
    public static int DbType(this ColumnType type) => type switch
    {
        ColumnType.Int => 3,
        ColumnType.Long => 20,
        ColumnType.Real => 5,
        ColumnType.Text => 130,
        ColumnType.Bool => 11,
        ColumnType.FileTime => 64,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    internal static string SqlType(this ColumnType type) => type switch
    {
        ColumnType.Int => "integer",
        ColumnType.Long or ColumnType.FileTime => "bigint",
        ColumnType.Real => "double precision",
        ColumnType.Text => "text",
        ColumnType.Bool => "boolean",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    internal static partial Regex Word();
}

/// <summary>
/// One column of a rowset: the name the client sees, its type, and the SQL that produces it (always cast to the type, so the reader needs no guesses). The SQL belongs to the
/// developer who writes the query; the values of a request go in as parameters (<c>$1</c>, <c>$2</c>), never into the text.
/// </summary>
public sealed record Column(string Name, ColumnType Type, string Sql)
{
    /// <summary>A column produced by any SQL expression over the query's <c>From</c>.</summary>
    public static Column Of(string name, ColumnType type, string sql) => new(name, type, sql);

    /// <summary>The key of an SDE table (<c>key</c>, a 64-bit integer).</summary>
    public static Column Key(string name = "key", ColumnType type = ColumnType.Long) => new(name, type, "key");

    /// <summary>A field of an SDE record's document: <c>Field("groupID", ColumnType.Int, "groupID")</c>, or a nested one: <c>Field("typeName", ColumnType.Text, "name", "en")</c>.</summary>
    public static Column Field(string name, ColumnType type, params string[] path)
    {
        if (path.Length == 0 || path.Any(p => !ColumnTypes.Word().IsMatch(p)))
        {
            throw new ArgumentException($"not a usable document path for column {name}", nameof(path));
        }
        return new Column(name, type, $"doc #>> '{{{string.Join(',', path)}}}'");
    }

    /// <summary>The English text of an SDE field that is localized (<c>name.en</c>).</summary>
    public static Column English(string name, string field) => Field(name, ColumnType.Text, field, "en");
}

/// <summary>
/// A named read of rows from PostgreSQL (the SDE or any module's tables) that is turned into the client's rowset shapes by <see cref="ReadSide"/>. A service that has to return a list of
/// records declares the query once and asks for the shape; it does not write a loop that builds PyValues by hand. <c>From</c> may use <c>{sde}</c> (or the name of any schema the read
/// side was given) for a schema.
/// </summary>
public sealed record RowsetQuery(string Name, string From, IReadOnlyList<Column> Columns)
{
    /// <summary>A SQL condition over <c>From</c>, with the request's values as <c>$1</c>, <c>$2</c>….</summary>
    public string? Where { get; init; }

    public string? OrderBy { get; init; }
    public int? Limit { get; init; }

    /// <summary>The column that identifies a row (for an indexed rowset); by default the first.</summary>
    public string? KeyColumn { get; init; }

    /// <summary>Keep the result for the same arguments for the life of the process. For the static data, which does not change while the server runs; never for what players change.</summary>
    public bool Cache { get; init; }

    /// <summary>A query over one table of the static data (<c>sde.&lt;table&gt;</c>, whose rows are <c>(ord, key, doc)</c>), in file order unless ordered otherwise.</summary>
    public static RowsetQuery Sde(string name, string table, params Column[] columns)
    {
        if (!ColumnTypes.Word().IsMatch(table))
        {
            throw new ArgumentException($"not a usable table name: {table}", nameof(table));
        }
        return new RowsetQuery(name, $"{{sde}}.{table}", columns) { OrderBy = "ord", Cache = true };
    }
}
