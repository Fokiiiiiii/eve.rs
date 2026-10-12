using System.Reflection;
using Eve.Data;

namespace Eve.Identity.Tests;

public class MigrationsTests
{
    [Fact]
    public void The_identity_migrations_are_embedded_and_named_by_file()
    {
        var set = PgAccountDirectory.MigrationSet();
        Assert.Contains(set, m => m.Name == "0001_accounts" && m.Sql.Contains("CREATE TABLE accounts", StringComparison.Ordinal));
    }

    [Fact]
    public void Embedded_migrations_ignore_other_folders_and_other_files()
    {
        Assert.Empty(Migrations.Embedded(Assembly.GetExecutingAssembly(), "NoSuchFolder"));
    }

    [PgFact]
    public async Task Migrations_apply_once_in_name_order_and_are_recorded()
    {
        await using var schema = await PgSchema.CreateAsync(migrate: false);
        var set = new[]
        {
            new Migration("0002_second", "INSERT INTO t VALUES (2)"),
            new Migration("0001_first", "CREATE TABLE t (n int)"),
        };
        Assert.Equal(["0001_first", "0002_second"], await Migrations.ApplyAsync(schema.Source, schema.Name, set));
        Assert.Empty(await Migrations.ApplyAsync(schema.Source, schema.Name, set));
        Assert.Equal(1L, await schema.ScalarAsync<long>($"SELECT count(*) FROM {schema.Name}.t"));
        Assert.Equal(2L, await schema.ScalarAsync<long>($"SELECT count(*) FROM {schema.Name}.migrations"));

        // a later migration is applied alone
        var more = set.Append(new Migration("0003_third", "INSERT INTO t VALUES (3)")).ToArray();
        Assert.Equal(["0003_third"], await Migrations.ApplyAsync(schema.Source, schema.Name, more));
        Assert.Equal(2L, await schema.ScalarAsync<long>($"SELECT count(*) FROM {schema.Name}.t"));
    }

    [PgFact]
    public async Task A_failing_migration_leaves_nothing_behind_and_can_be_fixed()
    {
        await using var schema = await PgSchema.CreateAsync(migrate: false);
        var bad = new[] { new Migration("0001_a", "CREATE TABLE t (n int)"), new Migration("0002_b", "THIS IS NOT SQL") };
        await Assert.ThrowsAnyAsync<Exception>(() => Migrations.ApplyAsync(schema.Source, schema.Name, bad));
        // all or nothing: the first one is not recorded either
        Assert.False(await schema.ScalarAsync<bool>($"SELECT to_regclass('{schema.Name}.migrations') IS NOT NULL"));

        var good = new[] { new Migration("0001_a", "CREATE TABLE t (n int)"), new Migration("0002_b", "ALTER TABLE t ADD COLUMN m int") };
        Assert.Equal(["0001_a", "0002_b"], await Migrations.ApplyAsync(schema.Source, schema.Name, good));
    }

    [PgFact]
    public async Task Migrations_run_in_the_modules_own_schema()
    {
        await using var schema = await PgSchema.CreateAsync(migrate: false);
        // a table name no other test or run can have used, so the check does not depend on what else is in the database
        var table = $"owned_{Guid.NewGuid():N}";
        await Migrations.ApplyAsync(schema.Source, schema.Name, [new Migration("0001_a", $"CREATE TABLE {table} (n int)")]);
        Assert.True(await schema.ScalarAsync<bool>($"SELECT to_regclass('{schema.Name}.{table}') IS NOT NULL"));
        Assert.False(await schema.ScalarAsync<bool>($"SELECT to_regclass('public.{table}') IS NOT NULL"));
    }

    [PgFact]
    public async Task Processes_that_start_together_apply_each_migration_once()
    {
        await using var schema = await PgSchema.CreateAsync(migrate: false);
        var set = new[] { new Migration("0001_a", "CREATE TABLE t (n int)"), new Migration("0002_b", "INSERT INTO t VALUES (1)") };
        var runs = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Migrations.ApplyAsync(schema.Source, schema.Name, set)));
        Assert.Equal(2, runs.Sum(r => r.Count));
        Assert.Equal(1L, await schema.ScalarAsync<long>($"SELECT count(*) FROM {schema.Name}.t"));
    }

    [Fact]
    public async Task Bad_input_is_refused_before_touching_the_database()
    {
        await using var source = Npgsql.NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest("Host=127.0.0.1"));
        await Assert.ThrowsAsync<ArgumentException>(() => Migrations.ApplyAsync(source, "bad; name", []));
        await Assert.ThrowsAsync<ArgumentException>(() => Migrations.ApplyAsync(source, "ok", [new Migration("a", ""), new Migration("a", "")]));
    }
}
