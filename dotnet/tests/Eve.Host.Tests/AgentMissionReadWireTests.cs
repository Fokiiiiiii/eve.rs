using System.Text.Json.Nodes;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public sealed class AgentMissionReadWireTests
{
    private const long CharacterId = 90000021;
    private const long AgentId = 7004;
    private const long SolarSystemId = 30000165;

    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    [Fact]
    public async Task Agent_mgr_read_calls_dispatch_through_the_service_registry_with_client_wire_shapes()
    {
        var state = JsonNode.Parse("""
            {
              "completedCareerAgentIDs": { "7004": true },
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
                }
              },
              "missionsByAgentID": {}
            }
            """)!.AsObject();
        var messages = JsonNode.Parse("""
            { "start": { "430001": "UI/Agent/EpicArc/Start" } }
            """)!.AsObject();
        var agent = new AgentMissionAgentData(
            AgentId, 8, 24, 3, null, 1000036, false, true, 17, false, "", "",
            18, 1531, 1, SolarSystemId, 416, 4239, 20520);
        var service = new AgentMissionService(
            (_, _) => ValueTask.FromResult<JsonObject?>(state.DeepClone().AsObject()),
            (_, _, _) => ValueTask.FromResult<IReadOnlyDictionary<long, JsonObject>>(
                new Dictionary<long, JsonObject>()),
            (_, _) => ValueTask.FromResult<JsonArray?>(null),
            rpcCallbacks: new AgentMissionRpcCallbacks(
                ReadAgents: _ => ValueTask.FromResult<IReadOnlyList<AgentMissionAgentData>>([agent]),
                ReadDisabledMissions: _ => ValueTask.FromResult<IReadOnlyList<long>>([5101, 5102]),
                ReadEpicArcMessages: _ => ValueTask.FromResult<JsonObject?>(messages.DeepClone().AsObject()),
                ReadSolarSystemOfAgent: (agentId, _) =>
                    ValueTask.FromResult<long?>(agentId == AgentId ? SolarSystemId : null)));
        var registry = new ServiceRegistry();
        registry.Register(service);
        var sink = new Sink();
        var session = new ClientSession(sink) { UserId = 7, CharacterId = CharacterId };
        session.Set("charid", new PyInt(CharacterId));
        session.Set("userid", new PyInt(7));
        var dispatcher = new PacketDispatcher(registry, () => 1_797_000_000_000);

        async Task<PyValue> Call(long callId, string method, params PyValue[] args)
        {
            Assert.True(await dispatcher.DispatchAsync(Request(callId, method, args), session));
            return ResponseResult(sink.Packets[^1]);
        }

        Assert.False(service.HasMethod("AddField"));
        Assert.False(service.HasMethod("Filter"));

        Assert.Equal(Py.Rowset(
            [
                "agentID", "agentTypeID", "divisionID", "level", "stationID", "corporationID",
                "isLocatorAgent", "isInSpace", "careerID", "importantMission", "missionKind",
                "missionTypeLabel", "specialityID", "stationTypeID", "bloodlineID",
            ],
            [
                new PyList([
                    new PyInt(AgentId), new PyInt(8), new PyInt(24), new PyInt(3), PyNone.Instance,
                    new PyInt(1000036), PyBool.False, PyBool.True, new PyInt(17), PyBool.False,
                    new PyStr(""), new PyStr(""), new PyInt(18), new PyInt(1531), new PyInt(1),
                ]),
            ]),
            await Call(1, "GetAgents"));

        Assert.Equal(new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyInt(AgentId), PyBool.True),
            new KeyValuePair<PyValue, PyValue>(new PyInt(AgentId + 1), PyBool.False),
        ]), await Call(2, "GetCompletedCareerAgentIDs",
            new PyList([new PyInt(AgentId), new PyInt(AgentId + 1)])));
        Assert.Equal(new PyList([new PyInt(5101), new PyInt(5102)]),
            await Call(3, "GetDisabledMissions"));

        var arcStatus = Assert.IsType<PyDict>(await Call(4, "GetMyEpicArcStatus"));
        var arc = Assert.Single(arcStatus.Entries);
        Assert.Equal(new PyInt(1200), arc.Key);
        Assert.IsType<PyDict>(arc.Value);
        Assert.Equal(new PyDict([
            new KeyValuePair<PyValue, PyValue>(
                new PyStr("start"),
                new PyDict([
                    new KeyValuePair<PyValue, PyValue>(
                        new PyInt(430001), new PyStr("UI/Agent/EpicArc/Start")),
                ])),
        ]), await Call(5, "GetMessagesForEpicArcMissions"));

        var journal = Assert.IsType<PyTuple>(await Call(6, "GetMyJournalDetails"));
        Assert.Equal(2, journal.Items.Count);
        Assert.All(journal.Items, item => Assert.IsType<PyList>(item));
        Assert.Equal(new PyInt(SolarSystemId),
            await Call(7, "GetSolarSystemOfAgent", new PyInt(AgentId)));
    }

    private static PyValue Request(long callId, string method, params PyValue[] args)
    {
        var body = new PyTuple([
            new PyInt(1),
            new PyStr(method),
            new PyTuple(args),
            PyNone.Instance,
        ]);
        var payload = new PyTuple([
            new PyTuple([PyBool.True, new PySubStream(body)]),
        ]);
        var source = new MachoAddress(AddressKind.Client, CallId: new PyInt(callId), ClientId: new PyInt(42));
        var destination = new MachoAddress(
            AddressKind.Node, Service: "agentMgr", CallId: new PyInt(callId), NodeId: new PyInt(MachoMessage.ProxyNodeId));
        return new PyObject(
            new PyStr("carbon.common.script.net.machoNetPacket.CallReq"),
            new PyTuple([
                new PyInt(MachoMessage.CallReq),
                source.Encode(),
                destination.Encode(),
                new PyInt(7),
                payload,
                new PyDict([]),
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
            ]));
    }

    private static PyValue ResponseResult(PyValue response)
    {
        var packet = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(response).Args).Items;
        Assert.Equal(MachoMessage.CallRsp, ((PyInt)packet[0]).Value);
        var payload = Assert.IsType<PyTuple>(packet[4]);
        return Assert.IsType<PySubStream>(payload.Items[0]).Decoded!;
    }
}
