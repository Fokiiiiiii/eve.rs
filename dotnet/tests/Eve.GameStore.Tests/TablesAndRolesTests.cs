using System.Text.Json.Nodes;

namespace Eve.GameStore.Tests;

public class TablesAndRolesTests
{
    private static readonly JsonObject Tables = (JsonObject)Fixtures.Read("tables.json")!;
    private static readonly JsonObject Roles = (JsonObject)Fixtures.Read("roles.json")!;

    private static bool IsUndefined(JsonNode? node) => node is JsonObject o && o.ContainsKey("$undefined");

    private static string? Text(JsonNode? node) => IsUndefined(node) || node is null ? null : (string)node!;

    [Fact]
    public void Runtime_tables_are_the_reference_set()
    {
        var expected = ((JsonArray)Tables["runtimeTables"]!).Select(t => (string)t!).ToList();
        Assert.Equal(114, expected.Count);
        Assert.Equal(expected, GameStoreTables.RuntimeTables);
    }

    [Fact]
    public void Row_groups_are_the_reference_map()
    {
        var expected = (JsonObject)Tables["rowGroups"]!;
        Assert.Equal(expected.Count, GameStoreTables.RowGroups.Count);
        foreach (var (table, groups) in expected)
        {
            Assert.Equal(((JsonArray)groups!).Select(g => (string)g!), GameStoreTables.RowGroupsFor(table));
        }
        Assert.Null(GameStoreTables.RowGroupsFor("noSuchTable"));
        Assert.Null(GameStoreTables.RowGroupsFor(null));
        Assert.Equal((string)Tables["separator"]!, GameStoreTables.RowKeySeparator.ToString());
    }

    [Fact]
    public void Every_grouped_table_is_a_runtime_table()
    {
        Assert.All(GameStoreTables.RowGroups.Keys, table => Assert.Contains(table, GameStoreTables.RuntimeTables));
    }

    [Fact]
    public void Lease_constants_match_the_reference_policy()
    {
        var policy = (JsonObject)Tables["leasePolicy"]!;
        Assert.Equal(PersistenceStore.MinLeaseMs, (long)policy["minLeaseMs"]!);
        Assert.Equal(PersistenceStore.DefaultLeaseMs, (long)policy["defaultLeaseMs"]!);
    }

    [Fact]
    public void Role_normalisation_matches_the_reference()
    {
        foreach (var entry in (JsonArray)Roles["normalizeRole"]!)
        {
            var input = Text(entry!["input"]);
            Assert.Equal((string)entry["output"]!, PersistenceRoles.Normalize(input));
        }
    }

    [Fact]
    public void Table_owner_roles_match_the_reference()
    {
        foreach (var entry in (JsonArray)Roles["ownerRoleForTable"]!)
        {
            Assert.Equal((string)entry!["role"]!, PersistenceRoles.OwnerRoleForTable(Text(entry["table"])));
        }
    }

    [Fact]
    public void Who_may_mutate_which_table_matches_the_reference()
    {
        var entries = (JsonArray)Roles["mayMutate"]!;
        Assert.True(entries.Count > 50);
        foreach (var entry in entries)
        {
            var role = Text(entry!["role"]);
            var table = Text(entry["table"]);
            Assert.True(
                (bool)entry["mayMutate"]! == PersistenceRoles.MayMutateTable(role, table),
                $"role={role} table={table}");
        }
        foreach (var entry in (JsonArray)Roles["mayDriveWorldState"]!)
        {
            Assert.Equal((bool)entry!["mayDrive"]!, PersistenceRoles.MayDriveWorldState(Text(entry["role"])));
        }
    }

    [Fact]
    public void A_reader_may_not_mutate_and_the_error_says_who_owns_the_table()
    {
        var error = Assert.Throws<PersistenceException>(() => PersistenceRoles.AssertMayMutateTable("reader", "scheduledJobs", "write"));
        Assert.Equal(PersistenceCodes.ProcessOwnershipViolation, error.Code);
        Assert.Equal("scheduler", error.Details["ownerRole"]);
    }

    [Fact]
    public void Configured_roles_accept_known_names_and_reject_the_rest()
    {
        Assert.Equal("reader", PersistenceRoles.Configured(null));
        Assert.Equal("reader", PersistenceRoles.Configured("  "));
        Assert.Equal("world", PersistenceRoles.Configured(" World "));
        Assert.Equal(PersistenceCodes.ProcessRoleInvalid, Assert.Throws<PersistenceException>(() => PersistenceRoles.Configured("admin")).Code);
    }

    [Theory]
    [InlineData("characters", true)]
    [InlineData("a_b9", true)]
    [InlineData("", false)]
    [InlineData("a-b", false)]
    [InlineData("a b", false)]
    [InlineData("a\"b", false)]
    [InlineData("a\n", false)]
    [InlineData("日本", false)]
    public void Only_plain_table_names_are_safe(string name, bool safe) => Assert.Equal(safe, TableNames.IsSafe(name));

    [Fact]
    public void Names_longer_than_postgres_identifiers_are_rejected()
    {
        Assert.True(TableNames.IsSafe(new string('a', 63)));
        Assert.False(TableNames.IsSafe(new string('a', 64)));
    }

    [Theory]
    [InlineData("_migrations")]
    [InlineData("_Persistence_Outbox")]
    [InlineData("_persistence_owners")]
    [InlineData("sqlite_master")]
    public void Internal_tables_cannot_be_named_for_persistence(string name) =>
        Assert.Throws<ArgumentException>(() => TableNames.RequirePersistence(name));

    [Theory]
    [InlineData("postgres://postgres@127.0.0.1:5432/eve", "Host=127.0.0.1;Port=5432;Database=eve;Username=postgres")]
    [InlineData("postgresql://u:p%40ss@db.example/app?sslmode=require", "Host=db.example;Database=app;Username=u;Password=p@ss;SSL Mode=Require")]
    [InlineData("Host=x;Database=y", "Host=x;Database=y")]
    public void Connection_urls_become_keyword_strings(string input, string expected)
    {
        var actual = new Npgsql.NpgsqlConnectionStringBuilder(PgConnection.ToConnectionString(input));
        var wanted = new Npgsql.NpgsqlConnectionStringBuilder(expected);
        Assert.Equal(wanted.ConnectionString, actual.ConnectionString);
    }
}
