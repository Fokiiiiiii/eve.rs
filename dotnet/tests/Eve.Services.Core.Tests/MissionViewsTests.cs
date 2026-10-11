using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Eve.Agents;
using Eve.Marshal;
using Eve.Sde;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

/// <summary>The client's text IDs (a synthetic export, never the client's own data) and the briefing and objective answers of a held kill mission.</summary>
public class MissionViewsTests
{
    private static MemoryStream Gzip(string text)
    {
        var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            gzip.Write(bytes, 0, bytes.Length);
        }
        buffer.Position = 0;
        return buffer;
    }

    [Fact]
    public void The_message_ids_are_read_and_a_row_that_is_not_json_is_counted_and_skipped()
    {
        var export = """
            {"_key": 9201, "messages": {"messages.mission.briefing": 140982}}
            {"_key": 9202, "messages": "not an object"}
            """;
        var (messages, skipped) = ClientMissionMessages.Parse(Gzip(export));
        Assert.Equal(140982, messages[9201][ClientMissionMessages.BriefingKey]);
        Assert.False(messages.ContainsKey(9202));
        Assert.Equal(1, skipped);
    }

    private const string KillStep = """
        {"_key": 9301, "name": {"en": "Kill Step"}, "nameID": 57289, "factionID": 500021,
         "killMission": {"dungeonID": 3509, "objectiveQuantity": 0},
         "missionRewards": {"reward": {"rewardTypeID": 29, "rewardQuantity": 100000}}}
        """;

    private static MissionStatic Kill() => MissionStaticCatalog.FromRows(3405148,
        [new SdeRow(9301, JsonDocument.Parse(KillStep).RootElement.Clone())], []).Mission(9301)!;

    [Fact]
    public void The_briefing_of_a_held_mission_carries_its_title_and_briefing_IDs()
    {
        var view = new HeldMissionView(Kill(), new AgentMissionRecord(1, 2, AgentMissionState.Accepted,
            new AgentMissionContent(9301, 57289, "Kill Step", "", false), null, null, null, null, null, null), 140982);
        var fields = MissionViews.Briefing(view).Entries.ToDictionary(entry => ((PyStr)entry.Key).Value, entry => entry.Value);
        Assert.Equal(57289, ((PyInt)fields["Mission Title ID"]).Value);
        Assert.Equal(140982, ((PyInt)fields["Mission Briefing ID"]).Value);
    }

    [Fact]
    public void The_objective_of_a_held_kill_mission_names_its_dungeon_and_the_ISK_reward()
    {
        var view = new HeldMissionView(Kill(), new AgentMissionRecord(1, 2, AgentMissionState.Accepted,
            new AgentMissionContent(9301, 57289, "Kill Step", "", false), null, null, null, null, null, null), null);
        var fields = MissionViews.Objective(view).Entries.ToDictionary(entry => ((PyStr)entry.Key).Value, entry => entry.Value);
        var dungeon = Assert.IsType<PyDict>(Assert.IsType<PyList>(fields["dungeons"]).Items[0]);
        Assert.Equal(3509, ((PyInt)dungeon.Entries.Single(entry => ((PyStr)entry.Key).Value == "dungeonID").Value).Value);
        Assert.Equal(500021, ((PyInt)dungeon.Entries.Single(entry => ((PyStr)entry.Key).Value == "ownerID").Value).Value);
        var reward = Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyList>(fields["normalRewards"]).Items));
        Assert.Equal(100000, ((PyInt)reward.Items[1]).Value);
        Assert.Equal(2, ((PyInt)fields["missionState"]).Value);
    }

    /// <summary>Opt-in: reads the client export named by EVE_CLIENT_SDE_DIR (read only, nothing is copied). Skipped when it is not set.</summary>
    [Fact]
    public void The_client_export_gives_the_briefing_ID_of_mission_14117_when_it_is_configured()
    {
        var directory = Environment.GetEnvironmentVariable(ClientMissionMessages.DirectoryVariable);
        if (string.IsNullOrWhiteSpace(directory)) return;
        using var stream = File.OpenRead(Path.Combine(directory, "native", "missions.jsonl.gz"));
        var (messages, skipped) = ClientMissionMessages.Parse(stream);
        Assert.True(messages.Count > 2000);
        Assert.Equal(140982, messages[14117][ClientMissionMessages.BriefingKey]);
        Assert.True(skipped < 400);
    }
}
