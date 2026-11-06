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
        var agent = new AgentMissionAgentData(
            2, 8, 24, 3, null, 1000036, false, true, 17, false, "courier", "Courier",
            18, 1531, 1, 30000165, 416, 4239, 20520);
        var fields = MissionViews.Briefing(agent, view, DateTimeOffset.UnixEpoch)
            .Entries.ToDictionary(entry => ((PyStr)entry.Key).Value, entry => entry.Value);
        Assert.Equal(57289, ((PyInt)fields["Mission Title ID"]).Value);
        Assert.Equal(140982, ((PyInt)fields["Mission Briefing ID"]).Value);
    }

    [Fact]
    public void The_objective_of_a_held_kill_mission_names_its_dungeon_and_the_ISK_reward()
    {
        var view = new HeldMissionView(Kill(), new AgentMissionRecord(1, 2, AgentMissionState.Accepted,
            new AgentMissionContent(9301, 57289, "Kill Step", "", false), null, null, null, null, null, null), null,
            LoyaltyPoints: 153, DungeonLocation: new MissionDungeonLocation(30005204, 12.5, -8, 400));
        var fields = MissionViews.Objective(view).Entries.ToDictionary(entry => ((PyStr)entry.Key).Value, entry => entry.Value);
        var dungeon = Assert.IsType<PyDict>(Assert.IsType<PyList>(fields["dungeons"]).Items[0]);
        Assert.Equal(3509, ((PyInt)dungeon.Entries.Single(entry => ((PyStr)entry.Key).Value == "dungeonID").Value).Value);
        Assert.Equal(500021, ((PyInt)dungeon.Entries.Single(entry => ((PyStr)entry.Key).Value == "ownerID").Value).Value);
        var location = Assert.IsType<PyDict>(dungeon.Entries.Single(entry => ((PyStr)entry.Key).Value == "location").Value);
        Assert.Equal(30005204, ((PyInt)location.Entries.Single(entry => ((PyStr)entry.Key).Value == "locationID").Value).Value);
        Assert.Equal(30005204, ((PyInt)location.Entries.Single(entry => ((PyStr)entry.Key).Value == "solarsystemID").Value).Value);
        Assert.Equal(2, ((PyInt)location.Entries.Single(entry => ((PyStr)entry.Key).Value == "agentID").Value).Value);
        var coordinates = Assert.IsType<PyTuple>(location.Entries.Single(entry => ((PyStr)entry.Key).Value == "coords").Value);
        Assert.Equal(12.5, ((PyReal)coordinates.Items[0]).Value);
        Assert.Equal(-8, ((PyReal)coordinates.Items[1]).Value);
        Assert.Equal(400, ((PyReal)coordinates.Items[2]).Value);
        var locations = Assert.IsType<PyList>(fields["locations"]);
        Assert.Equal(30005204, ((PyInt)Assert.Single(locations.Items)).Value);
        var reward = Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyList>(fields["normalRewards"]).Items));
        Assert.Equal(100000, ((PyInt)reward.Items[1]).Value);
        Assert.Equal(2, ((PyInt)fields["missionState"]).Value);
        Assert.Equal(153, ((PyInt)fields["loyaltyPoints"]).Value);
    }

    [Fact]
    public void The_objective_of_a_held_fetch_step_is_the_client_fetch_entry_with_the_drop_off_and_the_cargo()
    {
        var view = new HeldMissionView(Kill(), new AgentMissionRecord(1, 2, AgentMissionState.Accepted,
            new AgentMissionContent(9301, 57289, "Kill Step", "", false), null, null, null, null, null, null), null,
            new FetchObjectiveView(30755, 1, 0.1, 0, 60015249, 1000035));
        var fields = MissionViews.Objective(view).Entries.ToDictionary(entry => ((PyStr)entry.Key).Value, entry => entry.Value);
        var fetch = Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyList>(fields["objectives"]).Items));
        Assert.Equal("fetch", ((PyStr)fetch.Items[0]).Value);
        var target = Assert.IsType<PyTuple>(fetch.Items[1]);
        Assert.Equal(1000035, ((PyInt)target.Items[0]).Value);
        var location = Assert.IsType<PyDict>(target.Items[1]);
        Assert.Equal(60015249, ((PyInt)location.Entries.Single(entry => ((PyStr)entry.Key).Value == "locationID").Value).Value);
        var cargo = Assert.IsType<PyDict>(target.Items[2]);
        Assert.Equal(1, ((PyInt)cargo.Entries.Single(entry => ((PyStr)entry.Key).Value == "quantity").Value).Value);
        Assert.False(((PyBool)cargo.Entries.Single(entry => ((PyStr)entry.Key).Value == "hasCargo").Value).Value);
        Assert.Equal(0.1, ((PyReal)cargo.Entries.Single(entry => ((PyStr)entry.Key).Value == "volume").Value).Value, 10);
    }

    /// <summary>The embedded client copy (build 3396210): the message IDs of 2,886 missions (6 rows carry none), the briefing of mission 14117 and the templates.</summary>
    [Fact]
    public void The_embedded_client_data_gives_the_message_IDs_and_the_templates_of_the_build()
    {
        var (messages, skipped) = ClientMissionMessages.Parse(ClientMissionMessages.Open(""));
        Assert.Equal(2886, messages.Count);
        Assert.Equal(6, skipped);
        Assert.Equal(140982, messages[14117][ClientMissionMessages.BriefingKey]);

        var templates = ClientMissionTemplates.Parse(ClientMissionMessages.Open(""));
        Assert.Equal(2892, templates.Count);
        Assert.Equal(443, templates.Values.Count(template => template == ClientMissionTemplates.TradeTemplate));
        Assert.Equal(42, templates.Values.Count(template => template == ClientMissionTemplates.MiningTemplate));
    }
}
