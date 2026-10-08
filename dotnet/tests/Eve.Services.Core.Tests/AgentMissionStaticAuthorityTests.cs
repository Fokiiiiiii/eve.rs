using System.Text.Json.Nodes;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class AgentMissionStaticAuthorityTests
{
    [Fact]
    public async Task Agent_mgr_returns_non_empty_disabled_plausible_and_epic_arc_data()
    {
        var policy = JsonNode.Parse("""
            { "disabledMissions": [{ "missionID": 4743, "templateIDs": ["eve-survival:NewSlaves1"] }] }
            """)!.AsObject();
        var agents = JsonNode.Parse("""
            {
              "agentsByID": {
                "100": { "agentID": 100, "missionKind": "encounter", "level": 1 },
                "101": { "agentID": 101, "missionKind": "courier", "level": 1, "missionTemplateIDs": ["client-dungeon:courier"] },
                "102": { "agentID": 102, "agentTypeID": 10, "missionKind": "encounter", "level": 1 }
              }
            }
            """)!.AsObject();
        var authority = JsonNode.Parse("""
            {
              "missionsByID": {
                "7001": { "missionID": 7001, "missionKind": "encounter", "missionFlavor": "basic", "missionLevel": 1, "clientDungeonAvailable": true, "contentTemplate": "client-dungeon:7001" },
                "7002": { "missionID": 7002, "missionKind": "encounter", "missionFlavor": "basic", "missionLevel": 1, "clientDungeonAvailable": false, "contentTemplate": "client-dungeon:7002" },
                "7003": { "missionID": 7003, "missionKind": "courier", "missionFlavor": "basic", "missionLevel": 1, "contentTemplate": "client-dungeon:courier", "courierMission": { "objectiveTypeID": 34, "objectiveQuantity": 1 } },
                  "14117": {
                    "missionID": 14117,
                    "isEpicArc": true,
                    "missionFlavor": "epicArc",
                    "sourceAgentID": 102,
                    "contentTemplate": "agent.missionTemplatizedContent_EpicArcKillMission",
                    "messages": {
                      "messages.epicMission.journalText.chapterTitle": 141226,
                      "messages.epicMission.journalText.inProgressMessage": 140983,
                      "messages.epicMission.journalText.completedMessage": 140853
                    }
                  }
              },
              "indexes": {
                "agentIDToMissionIDs": { "100": [7001, 7002] },
                "missionTemplateToMissionIDs": { "client-dungeon:courier": [7003] },
                "preferredMissionIDs": { "basicCourier": 7003, "epicArcEncounter": 14117 }
              }
            }
            """)!.AsObject();

        var callbacks = new AgentMissionRpcCallbacks(
            ReadDisabledMissions: _ => ValueTask.FromResult(AgentMissionStaticAuthority.DisabledMissionIds(policy)),
            ReadPlausibleMissionIds: (agentId, _) => ValueTask.FromResult(
                AgentMissionStaticAuthority.PlausibleMissionIds(agents, authority, agentId)),
            ReadEpicArcMessages: _ => ValueTask.FromResult<JsonObject?>(
                AgentMissionStaticAuthority.EpicArcMessageMaps(authority)));
        var service = new AgentMissionService(
            (_, _) => ValueTask.FromResult<JsonObject?>(null),
            (_, _, _) => ValueTask.FromResult<IReadOnlyDictionary<long, JsonObject>>(new Dictionary<long, JsonObject>()),
            (_, _) => ValueTask.FromResult<JsonArray?>(null),
            rpcCallbacks: callbacks);
        var session = new ClientSession { CharacterId = 90000001 };

        var disabled = Assert.IsType<PyList>(await Call(service, session, "GetDisabledMissions"));
        Assert.Equal(new PyInt(4743), Assert.Single(disabled.Items));

        var agentSpecific = Assert.IsType<PyList>(await Call(service, session, "GetPlausibleMissionIDs", new PyInt(100)));
        Assert.Equal([new PyInt(7001)], agentSpecific.Items);
        var templateBound = Assert.IsType<PyList>(await Call(service, session, "GetPlausibleMissionIDs", new PyInt(101)));
        Assert.Equal([new PyInt(7003)], templateBound.Items);
        var epicArc = Assert.IsType<PyList>(await Call(service, session, "GetPlausibleMissionIDs", new PyInt(102)));
        Assert.Equal([new PyInt(14117)], epicArc.Items);

        var messages = Assert.IsType<PyDict>(await Call(service, session, "GetMessagesForEpicArcMissions"));
        var chapterTitles = Assert.IsType<PyDict>(Field(messages, "messages.epicMission.journalText.chapterTitle"));
        var inProgress = Assert.IsType<PyDict>(Field(messages, "messages.epicMission.journalText.inProgressMessage"));
        var completed = Assert.IsType<PyDict>(Field(messages, "messages.epicMission.journalText.completedMessage"));
        Assert.Equal(new PyInt(141226), Field(chapterTitles, new PyInt(14117)));
        Assert.Equal(new PyInt(140983), Field(inProgress, new PyInt(14117)));
        Assert.Equal(new PyInt(140853), Field(completed, new PyInt(14117)));
    }

    private static Task<PyValue> Call(IService service, ClientSession session, string method, params PyValue[] args) =>
        service.CallAsync(new ServiceCall { Method = method, Args = args, Session = session }).AsTask();

    private static PyValue Field(PyDict dict, string key) => Field(dict, new PyStr(key));

    private static PyValue Field(PyDict dict, PyValue key) =>
        dict.Entries.Single(entry => entry.Key == key).Value;
}
