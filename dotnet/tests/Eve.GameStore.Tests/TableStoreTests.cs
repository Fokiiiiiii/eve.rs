using System.Text.Json.Nodes;

namespace Eve.GameStore.Tests;

public class TableStoreTests
{
    private static RowUpsert Up(string key, string json) => new(key, json);

    private static async Task<OwnerFence> World(PgScope pg) =>
        await pg.Store.AcquireOwnerAsync("world", "a", new AcquireOptions(LeaseMs: 600_000));

    [PgFact]
    public async Task Rows_are_upserted_and_deleted()
    {
        await using var pg = await PgScope.CreateAsync();
        var fence = await World(pg);
        Assert.Equal(3, await pg.Store.ApplyChangesAsync("characters", [Up("1", """{"a":1}"""), Up("2", "2")], ["3"], fence));
        Assert.Equal(2, await pg.Store.RowCountAsync("characters"));
        Assert.Equal(1, await pg.Store.ApplyChangesAsync("characters", [], ["1"], fence));
        var rows = await pg.Store.LoadRowsAsync("characters");
        Assert.Equal("2", Assert.Single(rows).Key);
        Assert.Equal(0, await pg.Store.ApplyChangesAsync("characters", [], [], fence));
    }

    [PgFact]
    public async Task A_key_that_is_both_upserted_and_deleted_ends_up_deleted()
    {
        await using var pg = await PgScope.CreateAsync();
        var fence = await World(pg);
        await pg.Store.ApplyChangesAsync("characters", [Up("1", "1")], ["1"], fence);
        Assert.Equal(0, await pg.Store.RowCountAsync("characters"));
    }

    [PgFact]
    public async Task For_a_repeated_key_the_last_upsert_wins()
    {
        await using var pg = await PgScope.CreateAsync();
        var fence = await World(pg);
        await pg.Store.ApplyChangesAsync("characters", [Up("1", "1"), Up("1", "2"), Up("1", "3")], [], fence);
        Assert.Equal("3", Assert.Single(await pg.Store.LoadRowsAsync("characters")).Json);
    }

    [PgFact]
    public async Task The_revision_moves_only_when_the_document_changes()
    {
        await using var pg = await PgScope.CreateAsync();
        var fence = await World(pg);
        var table = pg.Q("characters");
        await pg.Store.ApplyChangesAsync("characters", [Up("1", """{"a":1,"b":[1,2]}""")], [], fence);
        Assert.Equal(1, await pg.ScalarAsync<long>($"SELECT rev FROM {table} WHERE key = '1'"));
        await pg.Store.ApplyChangesAsync("characters", [Up("1", """{ "b": [1, 2], "a": 1 }""")], [], fence); // same document
        Assert.Equal(1, await pg.ScalarAsync<long>($"SELECT rev FROM {table} WHERE key = '1'"));
        await pg.Store.ApplyChangesAsync("characters", [Up("1", """{"a":2,"b":[1,2]}""")], [], fence);
        Assert.Equal(2, await pg.ScalarAsync<long>($"SELECT rev FROM {table} WHERE key = '1'"));
    }

    [PgFact]
    public async Task Replacing_a_table_round_trips_flat_and_grouped_tables()
    {
        await using var pg = await PgScope.CreateAsync();
        var fence = await World(pg);
        var flat = Fixtures.Obj("""{"1":{"name":"日本語","n":9007199254740993},"2":null,"3":[1,2,{"x":true}],"4":"s","5":0.1}""");
        var grouped = Fixtures.Obj("""{"messages":{"10":{"s":"hi"},"11":null},"mailboxes":{"1":{"unread":2}},"mailingLists":{},"other":{"k":1}}""");

        Assert.Equal(5, await pg.Store.ReplaceAllAsync("characters", flat, fence));
        // Three group skeleton rows, three entities, and the one flat row.
        Assert.Equal(7, await pg.Store.ReplaceAllAsync("mail", grouped, fence));
        Assert.Equal(Fixtures.Canon(flat), Fixtures.Canon(await pg.Store.LoadTableAsync("characters")));
        Assert.Equal(Fixtures.Canon(grouped), Fixtures.Canon(await pg.Store.LoadTableAsync("mail")));
        // Entities of a group are separate rows.
        Assert.Equal(1, await pg.ScalarAsync<long>($"SELECT count(*) FROM {pg.Q("mail")} WHERE key = 'messages' || chr(31) || '10'"));
    }

    [PgFact]
    public async Task Replacing_a_table_removes_rows_that_are_no_longer_there()
    {
        await using var pg = await PgScope.CreateAsync();
        var fence = await World(pg);
        await pg.Store.ReplaceAllAsync("characters", Fixtures.Obj("""{"1":1,"2":2,"3":3}"""), fence);
        await pg.Store.ReplaceAllAsync("characters", Fixtures.Obj("""{"2":20}"""), fence);
        Assert.Equal("""{"2":20}""", Fixtures.Canon(await pg.Store.LoadTableAsync("characters")));
        await pg.Store.ReplaceAllAsync("characters", [], fence);
        Assert.Equal(0, await pg.Store.RowCountAsync("characters"));
    }

    [PgFact]
    public async Task A_failed_replace_leaves_the_table_untouched()
    {
        await using var pg = await PgScope.CreateAsync();
        var fence = await World(pg);
        await pg.Store.ReplaceAllAsync("characters", Fixtures.Obj("""{"1":1}"""), fence);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pg.Store.ReplaceAllAsync("mail", Fixtures.Obj("""{"messages":{"a\u001fb":1}}"""), fence));
        var nul = Fixtures.Obj("""{"1":"a\u0000b"}""");
        var error = await Assert.ThrowsAsync<PersistenceException>(() => pg.Store.ReplaceAllAsync("characters", nul, fence));
        Assert.Equal(PersistenceCodes.UnsupportedJson, error.Code);
        Assert.Equal("""{"1":1}""", Fixtures.Canon(await pg.Store.LoadTableAsync("characters")));
    }

    [PgFact]
    public async Task A_missing_table_is_created_for_a_writer_and_an_error_for_a_reader()
    {
        await using var pg = await PgScope.CreateAsync();
        var error = await Assert.ThrowsAsync<PersistenceException>(() => pg.Store.LoadRowsAsync("nope", createIfMissing: false));
        Assert.Equal(PersistenceCodes.TableNotFound, error.Code);
        Assert.False(await pg.Store.TableExistsAsync("nope"));
        Assert.Empty(await pg.Store.LoadRowsAsync("nope"));
        Assert.True(await pg.Store.TableExistsAsync("nope"));
        Assert.Empty(await pg.Store.LoadRowsAsync("nope", createIfMissing: false));
    }

    [PgFact]
    public async Task The_migration_marker_is_idempotent_and_fenced()
    {
        await using var pg = await PgScope.CreateAsync();
        var fence = await World(pg);
        Assert.False(await pg.Store.IsMigratedAsync("characters"));
        await pg.Store.MarkMigratedAsync("characters", fence);
        await pg.Store.MarkMigratedAsync("characters", fence);
        Assert.True(await pg.Store.IsMigratedAsync("characters"));
        pg.Advance(700_000);
        var error = await Assert.ThrowsAsync<PersistenceException>(() => pg.Store.MarkMigratedAsync("skills", fence));
        Assert.Equal(PersistenceCodes.OwnerStale, error.Code);
    }

    [PgFact]
    public async Task Several_processes_may_create_the_schema_and_a_table_at_once()
    {
        await using var pg = await PgScope.CreateAsync();
        var fence = await World(pg);
        var stores = Enumerable.Range(0, 6).Select(_ => pg.SecondStore()).ToList();
        try
        {
            await Task.WhenAll(stores.Select(s => s.EnsureSchemaAsync()));
            await Task.WhenAll(stores.Select(async (s, i) => await s.ApplyChangesAsync("characters", [Up($"k{i}", "1")], [], fence)));
            Assert.Equal(6, await pg.Store.RowCountAsync("characters"));
        }
        finally
        {
            foreach (var store in stores)
            {
                await store.DisposeAsync();
            }
        }
    }

    [PgFact]
    public async Task Table_names_are_validated_before_any_sql_runs()
    {
        await using var pg = await PgScope.CreateAsync();
        var fence = await World(pg);
        foreach (var name in new[] { "a\"; DROP TABLE x; --", "a b", "", new string('a', 64), "_migrations" })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => pg.Store.ApplyChangesAsync(name, [Up("1", "1")], [], fence));
            await Assert.ThrowsAsync<ArgumentException>(() => pg.Store.LoadRowsAsync(name));
        }
    }

    [PgFact]
    public async Task Bulk_replace_is_fast_enough_to_import_a_large_table()
    {
        await using var pg = await PgScope.CreateAsync();
        var fence = await World(pg);
        var table = new JsonObject();
        for (var i = 0; i < 20_000; i++)
        {
            table[i.ToString()] = new JsonObject { ["itemID"] = i, ["typeID"] = 587 + (i % 50), ["name"] = $"item {i}" };
        }
        var started = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(20_000, await pg.Store.ReplaceAllAsync("items", table, fence));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(20), $"took {started.Elapsed}");
        Assert.Equal(20_000, await pg.Store.RowCountAsync("items"));
    }
}
