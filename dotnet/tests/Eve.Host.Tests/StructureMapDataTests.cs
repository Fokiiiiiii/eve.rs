using System.Text.Json.Nodes;
using Eve.Host;
using Eve.Marshal;
using Eve.Sde;
using Npgsql;

namespace Eve.Host.Tests;

public sealed class StructureMapDataTests
{
    [PgFact]
    public async Task Structure_map_data_uses_the_pinned_type_table_and_returns_deployed_rows()
    {
        await using var source = NpgsqlDataSource.Create(
            Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        Assert.NotNull(await sde.BuildAsync());
        var type = Assert.Single(await sde.ReadAsync("types", [35832], CancellationToken.None));
        var readNames = new List<string>();
        var structures = new JsonObject
        {
            ["90000001"] = new JsonObject
            {
                ["structureID"] = 90000001,
                ["typeID"] = 35832,
                ["solarSystemID"] = 30000142,
                ["ownerCorpID"] = 98000001,
                ["itemName"] = "Map test Astrahus",
                ["position"] = new JsonObject { ["x"] = 1.5, ["y"] = -2.25, ["z"] = 3.75 },
            },
        };
        Task<JsonObject> ReadTable(string name, CancellationToken _)
        {
            readNames.Add(name);
            return Task.FromResult(name == "structures" ? structures : new JsonObject());
        }

        var model = new PersistenceStructureDirectoryReadModel(
            ReadTable,
            (_, _) => Task.FromResult(new Eve.Relations.Affiliation(null, null, null)),
            sde,
            null);

        var result = await model.GetStructureMapDataAsync(30000142, CancellationToken.None);

        var rowset = Assert.IsType<PyObjectEx>(result);
        Assert.Equal(ObjectExKind.Ex2, rowset.Kind);
        var row = Assert.IsType<PyPackedRow>(Assert.Single(rowset.List));
        Assert.Equal("structures", Assert.Single(readNames));
        Assert.Equal(type.Doc.GetProperty("groupID").GetInt32(), Assert.IsType<PyInt>(row.Values[0]).Value);
        Assert.Equal(35832, Assert.IsType<PyInt>(row.Values[1]).Value);
        Assert.Equal(90000001L, Assert.IsType<PyLong>(row.Values[2]).Value);
        Assert.Equal("Map test Astrahus", Assert.IsType<PyWString>(row.Values[3]).Value);
        Assert.Equal(30000142, Assert.IsType<PyInt>(row.Values[4]).Value);
        Assert.Equal(98000001, Assert.IsType<PyInt>(row.Values[5]).Value);
        Assert.Equal(1.5, Assert.IsType<PyReal>(row.Values[7]).Value);
        Assert.Equal(-2.25, Assert.IsType<PyReal>(row.Values[8]).Value);
        Assert.Equal(3.75, Assert.IsType<PyReal>(row.Values[9]).Value);
    }
}
