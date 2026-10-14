using System.Text.Json.Nodes;
using Eve.Host;
using Eve.Marshal;
using Eve.Macho;
using Eve.Relations;
using Eve.Services;

namespace Eve.Host.Tests;

public sealed class PersistenceStructureDirectoryCynoBeaconTests
{
    private const long CharacterId = 90_001;
    private const long CorporationId = 98_002;
    private const long StructureId = 30_000_142;
    private const long SolarSystemId = 30_000_142;

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task Jamming_filters_a_pharolux_from_the_accessible_list_and_system_map(bool jammed, int expectedCount)
    {
        var model = new PersistenceStructureDirectoryReadModel(
            ReadTable,
            (_, _) => Task.FromResult(new Affiliation(CorporationId, null, null)),
            null!,
            null,
            (_, _) => ValueTask.FromResult(jammed));
        var session = new ClientSession();
        session.Set("charid", new PyLong(CharacterId));
        var call = new ServiceCall
        {
            Method = "GetMyAccessibleOnlineCynoBeaconStructures",
            Args = [],
            Session = session,
            Cancellation = CancellationToken.None,
        };

        var accessible = Assert.IsType<PyList>(await model.GetMyAccessibleOnlineCynoBeaconStructuresAsync(call));
        var systems = Assert.IsType<PyList>(await model.GetSolarSystemsWithBeaconsAsync(CancellationToken.None));

        Assert.Equal(expectedCount, accessible.Items.Count);
        Assert.Equal(expectedCount, systems.Items.Count);
    }

    private static Task<JsonObject> ReadTable(string table, CancellationToken _)
    {
        if (table != "structures")
            return Task.FromResult(new JsonObject());
        return Task.FromResult(new JsonObject
        {
            [StructureId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject
            {
                ["structureID"] = StructureId,
                ["typeID"] = 35840,
                ["solarSystemID"] = SolarSystemId,
                ["ownerCorpID"] = CorporationId,
                ["ownerID"] = CorporationId,
                ["serviceStates"] = new JsonObject { ["16"] = 1 },
                ["itemName"] = "Pharolux",
                ["state"] = 6,
            },
        });
    }
}
