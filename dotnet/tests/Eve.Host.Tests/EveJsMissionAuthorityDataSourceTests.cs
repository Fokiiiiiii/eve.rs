using System.Text.Json.Nodes;
using Eve.Host;
using Xunit;

namespace Eve.Host.Tests;

public sealed class EveJsMissionAuthorityDataSourceTests
{
    [Fact]
    public void Reads_the_existing_evejs_authority_files_without_copying_them()
    {
        var root = Path.Combine(Path.GetTempPath(), "evejs-authority-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "_local", "gameStore", "data");
        var server = Path.Combine(root, "server");
        Directory.CreateDirectory(Path.Combine(data, "agentAuthority"));
        Directory.CreateDirectory(Path.Combine(data, "missionAuthority"));
        Directory.CreateDirectory(Path.Combine(server, "src", "config"));
        try
        {
            File.WriteAllText(Path.Combine(data, "agentAuthority", "data.json"), """
                { "agentsByID": { "100": { "agentID": 100, "missionKind": "encounter", "level": 1 } } }
                """);
            File.WriteAllText(Path.Combine(data, "missionAuthority", "data.json"), """
                {
                  "missionsByID": {
                    "7001": { "missionID": 7001, "missionKind": "encounter", "missionFlavor": "basic", "missionLevel": 1, "contentTemplate": "client-dungeon:7001" },
                    "14117": { "missionID": 14117, "missionFlavor": "epicArc", "messages": { "messages.epicMission.journalText.chapterTitle": 141226 } }
                  },
                  "indexes": { "agentIDToMissionIDs": { "100": [7001] } }
                }
                """);
            File.WriteAllText(Path.Combine(server, "src", "config", "productionMissionPolicy.json"), """
                { "disabledMissions": [{ "missionID": 4743 }] }
                """);

            var source = EveJsMissionAuthorityDataSource.LoadFromPaths(data, server);

            Assert.Equal([4743L], source.DisabledMissionIds);
            Assert.Equal([7001L], source.PlausibleMissionIds(100));
            Assert.Equal(141226L, source.EpicArcMessageMaps["messages.epicMission.journalText.chapterTitle"]?["14117"]?.GetValue<long>());
            Assert.Empty((source.EpicArcMessageMaps["messages.epicMission.journalText.inProgressMessage"] as JsonObject)!);
            Assert.Empty((source.EpicArcMessageMaps["messages.epicMission.journalText.completedMessage"] as JsonObject)!);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
