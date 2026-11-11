using Eve.Data;
using Eve.Marshal;
using Npgsql;

namespace Eve.ReadSide.Tests;

public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute() { if (Environment.GetEnvironmentVariable("EVE_PG_URL") is null) Skip = "EVE_PG_URL is not set"; }
}

public class ReadSideTests
{
    private static string Text(PyValue value) => value switch
    {
        PyStr s => s.Value, PyWString w => w.Value, PyToken t => t.Value, PyBytes b => System.Text.Encoding.UTF8.GetString(b.Value.Span),
        _ => throw new Xunit.Sdk.XunitException($"not text: {value}"),
    };

    private static long Num(PyValue value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        PyBigUnsigned integer => checked((long)new System.Numerics.BigInteger(integer.Bytes, isUnsigned: true, isBigEndian: false)),
        _ => throw new Xunit.Sdk.XunitException($"not a number: {value}"),
    };

    private static NpgsqlDataSource Source() => NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));

    private static RowsetQuery Groups() => RowsetQuery.Sde("groupsOfCategory", "groups", Column.Key("groupID", ColumnType.Int), Column.Field("categoryID", ColumnType.Int, "categoryID"), Column.English("groupName", "name"))
        with { Where = "(doc->>'categoryID')::int = $1", Cache = true };

    [Fact]
    public void The_sql_of_a_query_is_built_from_its_columns_and_names_the_schema()
    {
        var read = new ReadSide(null!).Register(Groups());
        Assert.Equal(
            "SELECT (key)::integer AS \"groupID\", (doc #>> '{categoryID}')::integer AS \"categoryID\", (doc #>> '{name,en}')::text AS \"groupName\" FROM sde.\"groups\" WHERE (doc->>'categoryID')::int = $1 ORDER BY ord",
            read.SqlOf("groupsOfCategory"));
    }

    [Fact]
    public void A_query_is_checked_when_it_is_registered_and_cannot_name_an_unknown_schema_or_a_bad_path()
    {
        var read = new ReadSide(null!);
        Assert.Throws<ArgumentException>(() => read.Register(new RowsetQuery("none", "sde.types", [])));
        Assert.Throws<ArgumentException>(() => read.Register(new RowsetQuery("dup", "sde.types", [Column.Key("a"), Column.Key("a")])));
        Assert.Throws<ArgumentException>(() => Column.Field("x", ColumnType.Text, "a'; DROP TABLE x; --"));
        Assert.Throws<ArgumentException>(() => RowsetQuery.Sde("bad", "groups; DROP TABLE groups", Column.Key()));
        read.Register(new RowsetQuery("elsewhere", "{market}.orders", [Column.Key()]));
        Assert.Throws<InvalidOperationException>(() => read.SqlOf("elsewhere"));
        Assert.Equal("SELECT (key)::bigint AS \"key\" FROM trade.orders", new ReadSide(null!, new Dictionary<string, string> { ["market"] = "trade" }).Register(new RowsetQuery("elsewhere", "{market}.orders", [Column.Key()])).SqlOf("elsewhere"));
        Assert.Throws<ArgumentException>(() => new ReadSide(null!, new Dictionary<string, string> { ["market"] = "trade; DROP" }));
        Assert.Throws<InvalidOperationException>(() => read.Register(new RowsetQuery("elsewhere", "x", [Column.Key()])));
        Assert.Throws<KeyNotFoundException>(() => read.SqlOf("missing"));
    }

    [Fact]
    public void A_configured_key_column_must_exist_before_query_registration()
    {
        var read = new ReadSide(null!);
        var error = Assert.Throws<ArgumentException>(() => read.Register(new RowsetQuery(
            "missingKeyColumn", "sde.types", [Column.Key("typeID")]) { KeyColumn = "ownerID" }));
        Assert.Contains("missingKeyColumn", error.Message);
        Assert.Contains("ownerID", error.Message);
    }

    [Fact]
    public void Keyed_rowset_conversions_report_a_missing_key_column_consistently()
    {
        var query = new RowsetQuery("missingKeyColumn", "unused", [Column.Key("typeID")])
        {
            KeyColumn = "ownerID",
        };
        var rows = RowSet.FromRows(query, [[7L]]);

        var keyValsError = Assert.Throws<InvalidOperationException>(() => rows.ToKeyValsByKey());
        Assert.Contains("missingKeyColumn", keyValsError.Message);
        Assert.Contains("ownerID", keyValsError.Message);
        var indexedError = Assert.Throws<InvalidOperationException>(() => rows.ToIndexedRowset());
        Assert.Contains("missingKeyColumn", indexedError.Message);
        Assert.Contains("ownerID", indexedError.Message);
    }

    [PgFact]
    public async Task Numeric_and_reserved_column_aliases_execute_as_quoted_postgres_identifiers()
    {
        await using var source = Source();
        var schema = "rs_" + Guid.NewGuid().ToString("N")[..12];
        await using var connection = await source.OpenConnectionAsync();
        await Sql.ExecuteAsync(connection, null, $"CREATE SCHEMA {schema}", CancellationToken.None);
        try
        {
            await Sql.ExecuteAsync(connection, null, $"CREATE TABLE {schema}.aliases (id integer NOT NULL)", CancellationToken.None);
            await Sql.ExecuteAsync(connection, null, $"INSERT INTO {schema}.aliases VALUES (7)", CancellationToken.None);
            var read = new ReadSide(source, new Dictionary<string, string> { ["mod"] = schema })
                .Register(new RowsetQuery("quotedAliases", "{mod}.aliases",
                [
                    Column.Of("123name", ColumnType.Int, "id"),
                    Column.Of("select", ColumnType.Int, "id"),
                ]) { OrderBy = "\"123name\" DESC, \"select\"" });

            Assert.Equal("SELECT (id)::integer AS \"123name\", (id)::integer AS \"select\" FROM " + schema + ".aliases ORDER BY \"123name\" DESC, \"select\"",
                read.SqlOf("quotedAliases"));
            var row = Assert.Single((await read.RowsAsync("quotedAliases")).Rows);
            Assert.Equal(7, (int)row[0]!);
            Assert.Equal(7, (int)row[1]!);
        }
        finally
        {
            await Sql.ExecuteAsync(connection, null, $"DROP SCHEMA {schema} CASCADE", CancellationToken.None);
        }
    }

    [PgFact]
    public async Task The_static_data_is_read_as_rows_and_the_second_ask_is_served_from_the_cache()
    {
        await using var source = Source();
        var read = new ReadSide(source).Register(Groups());
        var ships = await read.RowsAsync("groupsOfCategory", [6]);
        Assert.True(ships.Count > 20);
        Assert.All(ships.Rows, row => Assert.Equal(6, (int)row[1]!));
        Assert.Contains(ships.Rows, row => (string)row[2]! == "Frigate");
        Assert.Same(ships, await read.RowsAsync("groupsOfCategory", [6]));
        Assert.NotSame(ships, await read.RowsAsync("groupsOfCategory", [7]));
    }

    [PgFact]
    public async Task Mixed_case_sde_table_names_are_quoted_and_read_successfully()
    {
        await using var source = Source();
        var query = RowsetQuery.Sde("npcCorporations", "npcCorporations",
                Column.Key("corporationID", ColumnType.Int), Column.English("corporationName", "name"))
            with { Where = "key = $1" };
        var read = new ReadSide(source).Register(query);

        var rows = await read.RowsAsync("npcCorporations", ["1000001"]);
        var row = Assert.Single(rows.Rows);
        Assert.Equal(1000001, (int)row[0]!);
        Assert.Equal("Doomheim", (string)row[1]!);
        Assert.Same(rows, await read.RowsAsync("npcCorporations", ["1000001"]));
    }

    [PgFact]
    public async Task Cached_parameterized_queries_keep_argument_boundaries_types_and_nulls_independent()
    {
        await using var source = Source();
        var schema = "rs_" + Guid.NewGuid().ToString("N")[..12];
        await using var connection = await source.OpenConnectionAsync();
        await Sql.ExecuteAsync(connection, null, $"CREATE SCHEMA {schema}", CancellationToken.None);
        try
        {
            await Sql.ExecuteAsync(connection, null,
                $"CREATE TABLE {schema}.cache_arguments (id integer PRIMARY KEY, left_value text, right_value text)", CancellationToken.None);
            await Sql.ExecuteAsync(connection, null,
                $"INSERT INTO {schema}.cache_arguments VALUES (1, 'a|b', 'c'), (2, 'a', 'b|c'), (3, NULL, 'x'), (4, '', 'x')",
                CancellationToken.None);

            var read = new ReadSide(source, new Dictionary<string, string> { ["mod"] = schema })
                .Register(new RowsetQuery("cacheArguments", "{mod}.cache_arguments",
                    [Column.Of("rowID", ColumnType.Int, "id"), Column.Of("argumentType", ColumnType.Text, "pg_typeof($3)::text")])
                {
                    Where = "left_value IS NOT DISTINCT FROM $1::text AND right_value IS NOT DISTINCT FROM $2::text",
                    Cache = true,
                })
                .Register(new RowsetQuery("cacheNoArgs", "{mod}.cache_arguments",
                    [Column.Of("rowID", ColumnType.Int, "id")])
                {
                    OrderBy = "id",
                    Cache = true,
                });

            var args = new object?[] { "a|b", "c", 1 };
            var delimiterLeft = await read.RowsAsync("cacheArguments", args);
            Assert.Equal(1, (int)delimiterLeft.Rows.Single()[0]!);
            Assert.Equal("integer", (string)delimiterLeft.Rows.Single()[1]!);
            Assert.Same(delimiterLeft, await read.RowsAsync("cacheArguments", args));

            args[0] = "a";
            args[1] = "b|c";
            var delimiterRight = await read.RowsAsync("cacheArguments", args);
            Assert.Equal(2, (int)delimiterRight.Rows.Single()[0]!);
            Assert.Same(delimiterRight, await read.RowsAsync("cacheArguments", args));

            args[0] = "a|b";
            args[1] = "c";
            Assert.Same(delimiterLeft, await read.RowsAsync("cacheArguments", args));
            args[2] = 1L;
            var differentType = await read.RowsAsync("cacheArguments", args);
            Assert.Equal("bigint", (string)differentType.Rows.Single()[1]!);
            Assert.Same(differentType, await read.RowsAsync("cacheArguments", args));

            args[0] = null;
            args[1] = "x";
            args[2] = 1;
            var nullArgument = await read.RowsAsync("cacheArguments", args);
            Assert.Equal(3, (int)nullArgument.Rows.Single()[0]!);
            Assert.Same(nullArgument, await read.RowsAsync("cacheArguments", args));

            args[0] = "";
            var emptyArgument = await read.RowsAsync("cacheArguments", args);
            Assert.Equal(4, (int)emptyArgument.Rows.Single()[0]!);
            Assert.NotSame(nullArgument, emptyArgument);
            Assert.Same(emptyArgument, await read.RowsAsync("cacheArguments", args));

            var noArguments = await read.RowsAsync("cacheNoArgs");
            Assert.Equal(4, noArguments.Count);
            Assert.Same(noArguments, await read.RowsAsync("cacheNoArgs", []));
        }
        finally
        {
            await Sql.ExecuteAsync(connection, null, $"DROP SCHEMA {schema} CASCADE", CancellationToken.None);
        }
    }

    [PgFact]
    public async Task The_rows_become_a_rowset_that_the_client_reads_and_that_survives_the_codec()
    {
        await using var source = Source();
        var read = new ReadSide(source).Register(Groups());
        var ships = await read.RowsAsync("groupsOfCategory", [6]);
        var rowset = ships.ToRowset();
        var decoded = Assert.IsType<PyObject>(MarshalCodec.DecodeExact(MarshalCodec.Encode(rowset)));   // the codec writes it and reads it back
        Assert.Equal("eve.common.script.sys.rowset.Rowset", Text(decoded.Name));
        var dict = Assert.IsType<PyDict>(decoded.Args);
        PyValue Field(string key) => dict.Entries.First(e => Text(e.Key) == key).Value;
        Assert.Equal(["groupID", "categoryID", "groupName"], ((PyList)Field("header")).Items.Select(Text));
        var lines = (PyList)Field("lines");
        Assert.Equal(ships.Count, lines.Items.Count);
        Assert.Contains(lines.Items, line => line is PyList { Items: [PyInt, PyInt { Value: 6 }, PyWString { Value: "Frigate" }] });
    }

    [PgFact]
    public async Task The_rows_become_packed_rows_and_an_indexed_rowset_that_survive_the_codec()
    {
        await using var source = Source();
        var read = new ReadSide(source).Register(Groups() with { KeyColumn = "groupID" });
        var ships = await read.RowsAsync("groupsOfCategory", [6]);

        var packed = ships.ToPackedRows();
        Assert.Equal(ships.Count, packed.Count);
        var row = (PyPackedRow)MarshalCodec.DecodeExact(MarshalCodec.Encode(packed[0]));
        Assert.Equal([("groupID", 3L), ("categoryID", 3L), ("groupName", 130L)], row.Columns.Select(c => (c.Name, c.DbType)));
        Assert.Equal(6, ((PyInt)row.Values[1]).Value);

        var indexed = MarshalCodec.DecodeExact(MarshalCodec.Encode(ships.ToIndexedRowset()));
        var ex = Assert.IsType<PyObjectEx>(indexed);
        Assert.Equal(ships.Count, ex.Dict.Count);
        Assert.All(ex.Dict, entry => Assert.IsType<PyPackedRow>(entry.Value));
    }

    [PgFact]
    public async Task Nulls_booleans_reals_and_big_numbers_of_an_ordinary_table_come_through_with_the_right_types()
    {
        await using var source = Source();
        var schema = "rs_" + Guid.NewGuid().ToString("N")[..12];
        await using var connection = await source.OpenConnectionAsync();
        await Sql.ExecuteAsync(connection, null, $"CREATE SCHEMA {schema}", CancellationToken.None);
        try
        {
            await Sql.ExecuteAsync(connection, null, $"CREATE TABLE {schema}.things (id bigint PRIMARY KEY, name text, big bigint, ratio double precision, open boolean, at_ms bigint)", CancellationToken.None);
            await Sql.ExecuteAsync(connection, null,
                $"INSERT INTO {schema}.things VALUES (1, 'a', 9000000000, 0.5, true, 1700000000000), (2, NULL, NULL, NULL, false, 1700000001000), (3, 'c', 3, 2, NULL, 5)", CancellationToken.None);
            var read = new ReadSide(source, new Dictionary<string, string> { ["mod"] = schema }).Register(new RowsetQuery("things", "{mod}.things",
            [
                Column.Of("thingID", ColumnType.Long, "id"), Column.Of("name", ColumnType.Text, "name"), Column.Of("big", ColumnType.Long, "big"),
                Column.Of("ratio", ColumnType.Real, "ratio"), Column.Of("open", ColumnType.Bool, "open"),
                Column.Of("when", ColumnType.FileTime, "at_ms * 10000 + 116444736000000000"),
            ]) { OrderBy = "id", Where = "id >= $1" });
            var rows = await read.RowsAsync("things", [1L]);
            Assert.Equal(3, rows.Count);
            Assert.Equal(["a", null, "c"], rows.Rows.Select(r => (string?)r[1]));
            Assert.Equal([9000000000L, null, 3L], rows.Rows.Select(r => (long?)r[2]));
            Assert.Equal([0.5, null, 2.0], rows.Rows.Select(r => (double?)r[3]));
            Assert.Equal([true, false, null], rows.Rows.Select(r => (bool?)r[4]));

            var packed = rows.ToPackedRows();
            var first = (PyPackedRow)MarshalCodec.DecodeExact(MarshalCodec.Encode(packed[0]));
            Assert.IsType<PyInt>(first.Values[2]);
            Assert.Equal(9000000000L, Num(first.Values[2]));
            Assert.Equal(PyBool.True, first.Values[4]);
            Assert.IsType<PyBigUnsigned>(first.Values[5]);
            Assert.Equal(1700000000000L * 10000 + 116444736000000000, Num(first.Values[5]));
            var keyvals = (PyDict)rows.ToKeyValsByKey();
            Assert.Equal(3, keyvals.Entries.Count);
            Assert.IsType<PyNone>(((PyDict)((PyObject)keyvals.Entries[1].Value).Args).Entries.First(e => e.Key is PyStr { Value: "name" }).Value);
            Assert.IsType<PyObject>(rows.ToKeyVal());
        }
        finally
        {
            await Sql.ExecuteAsync(connection, null, $"DROP SCHEMA {schema} CASCADE", CancellationToken.None);
        }
    }
}
