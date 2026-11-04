using Eve.GameStore.Tests;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Eve.GameStore.Import.Tests;

public sealed class ImporterTests : IDisposable
{
    private static readonly JsonObject Expected = (JsonObject)Fixtures.Read("reference.expected.json")!;
    private readonly string _dir = Directory.CreateTempSubdirectory("eve-import-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A private copy of the reference database (a WAL database wants a writable directory).</summary>
    private string ReferenceCopy()
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.sqlite");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "gamestore", "reference.sqlite"), path);
        return path;
    }

    private static void Sql(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    private string NewSource(Action<SqliteConnection> fill)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.sqlite");
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            fill(connection);
        }
        SqliteConnection.ClearAllPools();
        return path;
    }

    private static void Exec(SqliteConnection connection, string sql, params (string, object)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        command.ExecuteNonQuery();
    }

    private static Dictionary<string, string> ExpectedRows(string node) =>
        ((JsonObject)Expected[node == "skillsWithJournal" ? "skillsWithJournal" : "tables"]!) is var container && node == "skillsWithJournal"
            ? container.ToDictionary(p => p.Key, p => (string)p.Value!)
            : ((JsonObject)container[node]!).ToDictionary(p => p.Key, p => (string)p.Value!);

    private static async Task<Dictionary<string, string>> StoredCanon(PgScope pg, string table) =>
        (await pg.Store.LoadRowsAsync(table, createIfMissing: false)).ToDictionary(r => r.Key, r => JsonCanonical.ToText(r.Json));

    private static Dictionary<string, string> Canon(Dictionary<string, string> rows) =>
        rows.ToDictionary(p => p.Key, p => JsonCanonical.ToText(p.Value));

    [PgFact]
    public async Task A_source_with_unacknowledged_journaled_batches_is_refused_by_default()
    {
        await using var pg = await PgScope.CreateAsync();
        var error = await Assert.ThrowsAsync<ImportException>(() => Importer.RunAsync(pg.Store, new ImportOptions(ReferenceCopy())));
        Assert.Contains("journaled batch", error.Message);
        Assert.Contains("skills", error.Message);
        Assert.False(await pg.Store.TableExistsAsync("characters")); // nothing was touched
    }

    [PgFact]
    public async Task The_reference_database_imports_with_its_journal_applied()
    {
        await using var pg = await PgScope.CreateAsync();
        var report = await Importer.RunAsync(pg.Store, new ImportOptions(ReferenceCopy(), ApplyOutbox: true));

        Assert.Equal(["characters", "mail", "skills"], report.Tables.Select(t => t.Table));
        Assert.All(report.Tables, t => Assert.True(t.Verified));
        Assert.Equal(1, report.Tables.Single(t => t.Table == "skills").JournalBatchesApplied);
        Assert.Equal(["characters"], report.Migrated);
        Assert.Equal(Canon(ExpectedRows("characters")), await StoredCanon(pg, "characters"));
        Assert.Equal(Canon(ExpectedRows("mail")), await StoredCanon(pg, "mail"));
        Assert.Equal(Canon(ExpectedRows("skillsWithJournal")), await StoredCanon(pg, "skills"));
        Assert.True(await pg.Store.IsMigratedAsync("characters"));
        Assert.False(await pg.Store.IsMigratedAsync("mail"));
    }

    [PgFact]
    public async Task Imported_tables_load_as_the_object_the_reference_server_would_cache()
    {
        await using var pg = await PgScope.CreateAsync();
        await Importer.RunAsync(pg.Store, new ImportOptions(ReferenceCopy(), ApplyOutbox: true));

        var mail = await pg.Store.LoadTableAsync("mail");
        Assert.Equal("hi", (string)mail["messages"]!["10"]!["subject"]!);
        Assert.Equal(2, (int)mail["mailboxes"]!["90000001"]!["unread"]!);
        Assert.Equal(12, (int)mail["nextMessageID"]!);
        Assert.Empty((JsonObject)mail["mailingLists"]!);
        var characters = await pg.Store.LoadTableAsync("characters");
        Assert.Equal("Ünï Cödé 日本語", (string)characters["90000001"]!["characterName"]!);
        Assert.Contains("9007199254740991", (await pg.Store.LoadRowsAsync("characters")).Single(r => r.Key == "90000003").Json);
    }

    [PgFact]
    public async Task A_running_source_is_refused_unless_told_otherwise()
    {
        await using var pg = await PgScope.CreateAsync();
        var source = ReferenceCopy();
        var future = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds();
        Sql(source, $"UPDATE _persistence_owners SET active = 1, lease_expires_at = {future}");
        var error = await Assert.ThrowsAsync<ImportException>(() => Importer.RunAsync(pg.Store, new ImportOptions(source, ApplyOutbox: true)));
        Assert.Contains("in use", error.Message);
        Assert.Contains("world", error.Message);
        await Importer.RunAsync(pg.Store, new ImportOptions(source, ApplyOutbox: true, IgnoreLease: true));
        Assert.Equal(3, (await pg.Store.LoadRowsAsync("characters")).Count);
    }

    [PgFact]
    public async Task A_target_with_a_live_server_is_refused()
    {
        await using var pg = await PgScope.CreateAsync();
        await pg.Store.AcquireOwnerAsync("world", "live-server", new AcquireOptions(LeaseMs: 600_000));
        var error = await Assert.ThrowsAsync<PersistenceException>(() => Importer.RunAsync(pg.Store, new ImportOptions(ReferenceCopy(), ApplyOutbox: true)));
        Assert.Equal(PersistenceCodes.OwnerConflict, error.Code);
        Assert.False(await pg.Store.TableExistsAsync("characters"));
    }

    [PgFact]
    public async Task A_target_table_with_rows_needs_replace_and_replace_removes_stale_rows()
    {
        await using var pg = await PgScope.CreateAsync();
        var source = ReferenceCopy();
        await Importer.RunAsync(pg.Store, new ImportOptions(source, ApplyOutbox: true));
        var error = await Assert.ThrowsAsync<ImportException>(() => Importer.RunAsync(pg.Store, new ImportOptions(source, ApplyOutbox: true)));
        Assert.Contains("already has rows", error.Message);

        var fence = await pg.Store.AcquireOwnerAsync("world", "w");
        await pg.Store.ApplyChangesAsync("characters", [new RowUpsert("stale", "1")], [], fence);
        await pg.Store.ReleaseOwnerAsync(fence);
        await Importer.RunAsync(pg.Store, new ImportOptions(source, ApplyOutbox: true, Replace: true));
        Assert.Equal(Canon(ExpectedRows("characters")), await StoredCanon(pg, "characters"));
    }

    [PgFact]
    public async Task Only_the_named_tables_are_imported_and_unknown_names_are_refused()
    {
        await using var pg = await PgScope.CreateAsync();
        var source = ReferenceCopy();
        var error = await Assert.ThrowsAsync<ImportException>(() => Importer.RunAsync(pg.Store, new ImportOptions(source, ["nope"], ApplyOutbox: true)));
        Assert.Contains("nope", error.Message);
        var report = await Importer.RunAsync(pg.Store, new ImportOptions(source, ["mail"], ApplyOutbox: true));
        Assert.Equal(["mail"], report.Tables.Select(t => t.Table));
        Assert.False(await pg.Store.TableExistsAsync("characters"));
        Assert.Empty(report.Migrated); // characters was not imported, so its marker is not copied
    }

    [PgFact]
    public async Task Tables_that_are_not_key_json_tables_are_skipped_and_reported()
    {
        await using var pg = await PgScope.CreateAsync();
        var source = NewSource(c =>
        {
            Exec(c, "CREATE TABLE characters (key TEXT PRIMARY KEY, json TEXT NOT NULL)");
            Exec(c, "INSERT INTO characters VALUES ('1', '{\"a\":1}')");
            Exec(c, "CREATE TABLE odd (id INTEGER, value TEXT)");
            Exec(c, "CREATE TABLE \"bad-name\" (key TEXT, json TEXT)");
        });
        var report = await Importer.RunAsync(pg.Store, new ImportOptions(source));
        Assert.Equal(["characters"], report.Tables.Select(t => t.Table));
        Assert.Equal(["bad-name", "odd"], report.Skipped.Select(s => s.Table).Order());
    }

    [PgFact]
    public async Task A_bad_row_fails_that_table_without_touching_it()
    {
        await using var pg = await PgScope.CreateAsync();
        var fence = await pg.Store.AcquireOwnerAsync("world", "w");
        await pg.Store.ApplyChangesAsync("characters", [new RowUpsert("keep", "1")], [], fence);
        await pg.Store.ReleaseOwnerAsync(fence);
        var source = NewSource(c =>
        {
            Exec(c, "CREATE TABLE characters (key TEXT PRIMARY KEY, json TEXT NOT NULL)");
            Exec(c, "INSERT INTO characters VALUES ('1', '{\"a\":1}'), ('2', '{not json'), ('3', '3')");
        });
        var error = await Assert.ThrowsAsync<ImportException>(() => Importer.RunAsync(pg.Store, new ImportOptions(source, Replace: true)));
        Assert.Contains("characters", error.Message);
        Assert.Equal(["keep"], (await pg.Store.LoadRowsAsync("characters")).Select(r => r.Key));
    }

    [PgFact]
    public async Task A_document_with_a_nul_escape_is_reported_for_its_table()
    {
        await using var pg = await PgScope.CreateAsync();
        var source = NewSource(c =>
        {
            Exec(c, "CREATE TABLE characters (key TEXT PRIMARY KEY, json TEXT NOT NULL)");
            Exec(c, "INSERT INTO characters VALUES ('1', '\"a\\u0000b\"')");
        });
        var error = await Assert.ThrowsAsync<ImportException>(() => Importer.RunAsync(pg.Store, new ImportOptions(source)));
        Assert.Contains("characters", error.Message);
        Assert.Contains("\\u0000", error.Message);
    }

    [PgFact]
    public async Task Journaled_deletes_win_over_upserts_and_new_rows_are_appended()
    {
        await using var pg = await PgScope.CreateAsync();
        var source = NewSource(c =>
        {
            Exec(c, "CREATE TABLE skills (key TEXT PRIMARY KEY, json TEXT NOT NULL)");
            Exec(c, "INSERT INTO skills VALUES ('a', '1'), ('b', '2'), ('c', '3')");
            Exec(c, "CREATE TABLE _persistence_outbox (operation_id INTEGER PRIMARY KEY AUTOINCREMENT, table_name TEXT NOT NULL UNIQUE, owner_role TEXT, owner_epoch INTEGER, owner_instance_id TEXT, upserts_json TEXT NOT NULL, deletes_json TEXT NOT NULL, state TEXT, created_at TEXT, applied_at TEXT)");
            Exec(c, "INSERT INTO _persistence_outbox (table_name, owner_role, owner_epoch, owner_instance_id, upserts_json, deletes_json, state, created_at) VALUES ('skills', 'world', 1, 'x', '[[\"a\",\"10\"],[\"b\",\"20\"],[\"d\",\"40\"]]', '[\"b\",\"c\"]', 'pending', 'now')");
        });
        await Importer.RunAsync(pg.Store, new ImportOptions(source, ApplyOutbox: true));
        var rows = (await pg.Store.LoadRowsAsync("skills")).ToDictionary(r => r.Key, r => r.Json);
        Assert.Equal(["a", "d"], rows.Keys.Order());
        Assert.Equal("10", rows["a"]);
        Assert.Equal("40", rows["d"]);
    }

    [PgFact]
    public async Task A_large_table_imports_and_verifies()
    {
        await using var pg = await PgScope.CreateAsync();
        var source = NewSource(c =>
        {
            Exec(c, "CREATE TABLE items (key TEXT PRIMARY KEY, json TEXT NOT NULL)");
            using var tx = c.BeginTransaction();
            using var command = c.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "INSERT INTO items VALUES ($k, $j)";
            var key = command.Parameters.Add("$k", SqliteType.Text);
            var json = command.Parameters.Add("$j", SqliteType.Text);
            for (var i = 0; i < 40_000; i++)
            {
                key.Value = i.ToString();
                json.Value = $$"""{"itemID":{{i}},"typeID":{{587 + i % 50}},"name":"item {{i}}","flags":[1,2,3]}""";
                command.ExecuteNonQuery();
            }
            tx.Commit();
        });
        var started = Stopwatch.StartNew();
        var report = await Importer.RunAsync(pg.Store, new ImportOptions(source));
        Assert.Equal(40_000, report.Tables.Single().Rows);
        Assert.True(report.Tables.Single().Verified);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(60), $"took {started.Elapsed}");
    }

    [PgFact]
    public async Task The_command_line_reports_success_and_failure_with_exit_codes()
    {
        await using var pg = await PgScope.CreateAsync();
        var dll = Path.Combine(AppContext.BaseDirectory, "eve-gamestore-import.dll");
        (int Code, string Output) Run(params string[] args)
        {
            var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add(dll);
            foreach (var arg in args)
            {
                info.ArgumentList.Add(arg);
            }
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output);
        }

        Assert.Equal(2, Run().Code);
        var missing = Run("--sqlite", Path.Combine(_dir, "nope.sqlite"), "--pg", PgScope.Url!, "--schema", pg.Schema);
        Assert.Equal(1, missing.Code);
        var refused = Run("--sqlite", ReferenceCopy(), "--pg", PgScope.Url!, "--schema", pg.Schema);
        Assert.Equal(1, refused.Code);
        Assert.Contains("journaled batch", refused.Output);
        var ok = Run("--sqlite", ReferenceCopy(), "--pg", PgScope.Url!, "--schema", pg.Schema, "--apply-outbox");
        Assert.True(ok.Code == 0, ok.Output);
        Assert.Contains("done: 3 tables", ok.Output);
        Assert.Equal(3, await pg.Store.RowCountAsync("characters"));
    }
}
