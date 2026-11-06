using System.Text.Json.Nodes;
using Eve.Host;

namespace Eve.Host.Tests;

public sealed class PersistenceFleetSettingsTests
{
    [Fact]
    public void Finds_the_named_setup_in_the_characterSettings_GameStore_row()
    {
        var characterTable = JsonNode.Parse("""
            {
              "9001": {
                "characterSettings": {
                  "fleetSetups": [
                    { "setupName": "Other", "wingsInfo": {} },
                    { "setupName": "Raid", "wingsInfo": { "0": { "wingIdx": 0 } } }
                  ]
                }
              }
            }
            """)!.AsObject();

        var setup = PersistenceFleetSettings.FindSetup(characterTable, 9001, " Raid ");

        Assert.Equal("Raid", setup?["setupName"]?.GetValue<string>());
        Assert.Null(PersistenceFleetSettings.FindSetup(characterTable, 9001, "raid"));
        Assert.Null(PersistenceFleetSettings.FindSetup(characterTable, 9002, "Raid"));
    }
}
