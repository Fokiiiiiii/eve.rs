using System.Text.Json.Nodes;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    private const long AgentRpcPilot = 90_000_041;
    private const long AgentRpcFirstAgent = 7_004;
    private const long AgentRpcSecondAgent = 7_005;
    private const long AgentRpcBookmark = 99_001;

    [Fact]
    public async Task Agent_mgr_read_rpcs_are_registered_on_the_host_and_return_client_consumed_shapes()
    {
        const long acceptedAt = 133801234567890123;
        var state = JsonNode.Parse("""
            {
              "completedCareerAgentIDs": { "7004": true, "7005": false },
              "epicArcProgress": {
                "missionStatusByArcID": {
                  "1200": {
                    "430001": {
                      "acceptedDate": "133801234567890123",
                      "completedDate": "",
                      "quitDate": "",
                      "nameID": 123456
                    }
                  }
                },
                "completedArcsByID": {}
              },
              "missionsByAgentID": {
                "7004": {
                  "agentID": 7004,
                  "runtimeStatus": "accepted",
                  "missionTitle": "Courier Assignment",
                  "missionTypeLabel": "UI/Agents/StandardMission/Courier",
                  "importantMission": true,
                  "expiresAtFileTime": "133801234567890123",
                  "objectiveMode": "transport",
                  "contentID": "courier-12",
                  "bookmarkIDsByRole": { "destination": 99001 },
                  "remoteCompletable": false,
                  "clientMissionRecord": { "remoteOfferable": true }
                },
                "7005": {
                  "agentID": 7005,
                  "runtimeStatus": "accepted",
                  "missionTitle": "Encounter Assignment",
                  "missionTypeLabel": "UI/Agents/StandardMission/Encounter",
                  "importantMission": false,
                  "expiresAtFileTime": "133801234567890123",
                  "objectiveMode": "dungeon",
                  "contentID": "123456",
                  "bookmarkIDsByRole": {},
                  "remoteCompletable": false,
                  "clientMissionRecord": { "remoteOfferable": true }
                }
              }
            }
            """)!.AsObject();
        var agent = new AgentMissionAgentData(
            AgentRpcFirstAgent, 8, 24, 3, 60_000_001, 1_000_036, false, false, 17, false,
            "courier", "courier", 18, 1_531, 1, 30_000_165, null, null, null);
        var bookmark = JsonNode.Parse("""
            {
              "itemID": 600001,
              "typeID": 5,
              "locationID": 30000142,
              "creatorID": 7004,
              "created": "133801234500000000",
              "x": 1.25,
              "y": 2.5,
              "z": 3.75,
              "metadata": {
                "agentID": 7004,
                "hint": "Mission destination",
                "locationType": "destination",
                "role": "destination",
                "solarsystemID": 30000142
              }
            }
            """)!.AsObject();
        var missionMessages = JsonNode.Parse("""
            { "start": { "430001": "UI/Agent/EpicArc/Start" } }
            """)!.AsObject();
        var service = new AgentMissionService(
            (characterId, _) => ValueTask.FromResult<JsonObject?>(
                characterId == AgentRpcPilot ? state : null),
            (characterId, bookmarkIds, _) =>
            {
                Assert.Equal(AgentRpcPilot, characterId);
                Assert.Equal([AgentRpcBookmark], bookmarkIds);
                return ValueTask.FromResult<IReadOnlyDictionary<long, JsonObject>>(
                    new Dictionary<long, JsonObject> { [AgentRpcBookmark] = bookmark });
            },
            (characterId, _) => ValueTask.FromResult<JsonArray?>(
                characterId == AgentRpcPilot
                    ? JsonNode.Parse("[[7004,3339,24.5,3.25,5,0,600001],[7005,3340,10,0,3,0,null]]")!.AsArray()
                    : null),
            rpcCallbacks: new AgentMissionRpcCallbacks(
                ReadAgents: _ => ValueTask.FromResult<IReadOnlyList<AgentMissionAgentData>>(
                    [agent, agent with { AgentId = AgentRpcSecondAgent, MissionKind = "encounter" }]),
                ReadDisabledMissions: _ => ValueTask.FromResult<IReadOnlyList<long>>([5101, 5102]),
                ReadEpicArcMessages: _ => ValueTask.FromResult<JsonObject?>(missionMessages),
                ReadSolarSystemOfAgent: (agentId, _) => ValueTask.FromResult<long?>(
                    agentId == AgentRpcFirstAgent ? 30_000_165 : null)));

        var machoVersion = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        Assert.False(service.HasMethod("Index"));
        Assert.False(service.HasMethod("get"));

        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.Set("charid", new PyInt(AgentRpcPilot)));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        async Task<PyValue> CallAsync(long callId, string method, PyValue args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, callId, "agentMgr", method, args, machoVersion))));
            var send = Assert.IsType<Send>(await front.ReceiveAsync());
            var decoded = MarshalCodec.Decode(send.Payload);
            var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded));
            Assert.Equal(MachoMessage.CallRsp, packet.Type);
            return Result(decoded)!;
        }

        var agents = await CallAsync(1, "GetAgents", new PyTuple([]));
        var expectedAgents = MarshalCodec.Decode(MarshalCodec.Encode(Py.Rowset(
            [
                "agentID", "agentTypeID", "divisionID", "level", "stationID", "corporationID",
                "isLocatorAgent", "isInSpace", "careerID", "importantMission", "missionKind",
                "missionTypeLabel", "specialityID", "stationTypeID", "bloodlineID",
            ],
            [
                new PyList([
                    new PyInt(AgentRpcFirstAgent), new PyInt(8), new PyInt(24), new PyInt(3),
                    new PyInt(60_000_001), new PyInt(1_000_036), PyBool.False, PyBool.False,
                    new PyInt(17), PyBool.False, new PyStr("courier"), new PyStr("courier"),
                    new PyInt(18), new PyInt(1_531), new PyInt(1),
                ]),
                new PyList([
                    new PyInt(AgentRpcSecondAgent), new PyInt(8), new PyInt(24), new PyInt(3),
                    new PyInt(60_000_001), new PyInt(1_000_036), PyBool.False, PyBool.False,
                    new PyInt(17), PyBool.False, new PyStr("encounter"), new PyStr("courier"),
                    new PyInt(18), new PyInt(1_531), new PyInt(1),
                ]),
            ])));
        Assert.Equal(expectedAgents, agents);

        var completed = Assert.IsType<PyDict>(await CallAsync(
            2, "GetCompletedCareerAgentIDs",
            new PyTuple([new PyList([new PyInt(AgentRpcFirstAgent), new PyInt(AgentRpcSecondAgent)])])));
        Assert.Equal(PyBool.True, Get(completed, new PyInt(AgentRpcFirstAgent)));
        Assert.Equal(PyBool.False, Get(completed, new PyInt(AgentRpcSecondAgent)));
        Assert.Equal(new PyList([new PyInt(5101), new PyInt(5102)]),
            await CallAsync(3, "GetDisabledMissions", new PyTuple([])));
        var messages = Assert.IsType<PyDict>(
            await CallAsync(4, "GetMessagesForEpicArcMissions", new PyTuple([])));
        var starts = Assert.IsType<PyDict>(Get(messages, new PyStr("start")));
        Assert.Equal("UI/Agent/EpicArc/Start", T(Get(starts, new PyInt(430001))));

        var arcStatus = Assert.IsType<PyDict>(
            await CallAsync(5, "GetMyEpicArcStatus", new PyTuple([])));
        var arcMissions = Assert.IsType<PyDict>(Get(arcStatus, new PyInt(1200)));
        var arcMission = Assert.IsType<PyObject>(Get(arcMissions, new PyInt(430001)));
        var arcMissionFields = Assert.IsType<PyDict>(arcMission.Args);
        Assert.Equal(Py.FileTimeLong(acceptedAt), Get(arcMissionFields, new PyStr("acceptedDate")));
        Assert.Equal(PyNone.Instance, Get(arcMissionFields, new PyStr("completedDate")));
        Assert.Equal(new PyInt(123456), Get(arcMissionFields, new PyStr("nameID")));

        var journal = Assert.IsType<PyTuple>(
            await CallAsync(6, "GetMyJournalDetails", new PyTuple([])));
        var allMissions = Assert.IsType<PyList>(journal.Items[0]);
        Assert.Equal(2, allMissions.Items.Count);
        var firstMission = Assert.IsType<PyTuple>(allMissions.Items[0]);
        var missionBookmarks = Assert.IsType<PyList>(firstMission.Items[6]);
        var returnedBookmark = Assert.IsType<PyObject>(Assert.Single(missionBookmarks.Items));
        Assert.Equal(new PyInt(600001), Get(Assert.IsType<PyDict>(returnedBookmark.Args), new PyStr("itemID")));
        var research = Assert.IsType<PyList>(journal.Items[1]);
        Assert.Equal(2, research.Items.Count);
        Assert.Equal(new PyInt(AgentRpcFirstAgent), Assert.IsType<PyTuple>(research.Items[0]).Items[0]);

        var binding = Assert.IsType<PyTuple>(await CallAsync(
            7, "MachoBindObject", new PyTuple([new PyTuple([new PyInt(AgentRpcFirstAgent)])])));
        var oid = BoundObjects.ExtractName(Assert.IsType<PySubStruct>(binding.Items[0]))!;
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.BoundCall(open, 8, oid, "GetMyJournalDetails", new PyTuple([]), machoVersion))));
        var boundResponse = Assert.IsType<Send>(await front.ReceiveAsync());
        var boundPayload = MarshalCodec.Decode(boundResponse.Payload);
        Assert.Equal(MachoMessage.CallRsp,
            Assert.IsType<MachoPacket>(MachoPacket.Decode(boundPayload)).Type);
        var boundJournal = Assert.IsType<PyTuple>(Result(boundPayload));
        var boundMissions = Assert.IsType<PyList>(boundJournal.Items[0]);
        var onlyAgentMission = Assert.IsType<PyTuple>(Assert.Single(boundMissions.Items));
        Assert.Equal(new PyInt(AgentRpcFirstAgent), onlyAgentMission.Items[4]);
        var boundResearch = Assert.IsType<PyList>(boundJournal.Items[1]);
        Assert.Equal(new PyInt(AgentRpcFirstAgent), Assert.IsType<PyTuple>(Assert.Single(boundResearch.Items)).Items[0]);

        Assert.Equal(new PyInt(30_000_165),
            await CallAsync(9, "GetSolarSystemOfAgent", new PyTuple([new PyInt(AgentRpcFirstAgent)])));
    }
    private static PyValue Get(PyDict dict, PyValue key) =>
        Assert.Single(dict.Entries, pair => pair.Key == key ||
            (pair.Key is PyBytes && key is PyStr && T(pair.Key) == T(key)) ||
            (pair.Key is PyStr && key is PyBytes && T(pair.Key) == T(key))).Value;
}
