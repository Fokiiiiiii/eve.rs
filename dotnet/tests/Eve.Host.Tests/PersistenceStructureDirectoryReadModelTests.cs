using System.Text.Json.Nodes;
using Xunit;

namespace Eve.Host.Tests;

public sealed class PersistenceStructureDirectoryReadModelTests
{
    [Fact]
    public async Task Structure_bulk_read_exposes_only_persisted_structure_names()
    {
        const long structureId = 1_000_000_000_001;
        var state = new JsonObject
        {
            [structureId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject
            {
                ["structureID"] = structureId,
                ["typeID"] = 35841,
                ["solarSystemID"] = 30000142,
                ["ownerCorpID"] = 1000002,
                ["itemName"] = "Stored Fortizar",
            },
        };
        var readModel = new PersistenceStructureDirectoryReadModel(
            (_, _) => Task.FromResult((JsonObject)state.DeepClone()),
            null!,
            null!,
            null!);

        var structures = await readModel.GetStructureInfosAsync([structureId, structureId + 1], CancellationToken.None);

        var structure = Assert.Single(structures);
        Assert.Equal(structureId, structure.Key);
        Assert.True(structure.Value.PersistentlyDeployed);
        Assert.Equal("Stored Fortizar", structure.Value.ItemName);
    }
}
