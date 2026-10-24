using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public sealed class AgentMgrLocalCollectionWireTests
{
    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    [Fact]
    public async Task Agent_mgr_collection_operations_are_local_and_parent_rpcs_dispatch_with_client_shapes()
    {
        var agent = new AgentMissionAgentData(
            7004, 8, 24, 3, null, 1000036, false, true, 17, false, "", "",
            18, 1531, 1, 30000165, 416, 4239, 20520);
        var messages = System.Text.Json.Nodes.JsonNode.Parse(
            """{"start":{"430001":"UI/Agent/EpicArc/Start"}}""")!.AsObject();
        var service = new AgentMissionService(
            (_, _) => ValueTask.FromResult<System.Text.Json.Nodes.JsonObject?>(null),
            (_, _, _) => ValueTask.FromResult<IReadOnlyDictionary<long, System.Text.Json.Nodes.JsonObject>>(
                new Dictionary<long, System.Text.Json.Nodes.JsonObject>()),
            (_, _) => ValueTask.FromResult<System.Text.Json.Nodes.JsonArray?>(null),
            rpcCallbacks: new AgentMissionRpcCallbacks(
                ReadAgents: _ => ValueTask.FromResult<IReadOnlyList<AgentMissionAgentData>>([agent]),
                ReadEpicArcMessages: _ => ValueTask.FromResult<System.Text.Json.Nodes.JsonObject?>(messages)));
        Assert.False(service.HasMethod("Index"));
        Assert.False(service.HasMethod("get"));

        var registry = new ServiceRegistry();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => 1_790_646_377_000);
        var sink = new Sink();
        var session = new ClientSession(sink) { UserId = 7 };
        session.Set("userid", new PyInt(7));

        Assert.True(await dispatcher.DispatchAsync(Request(1, "GetAgents"), session));
        Assert.Equal(Py.Rowset(
            [
                "agentID", "agentTypeID", "divisionID", "level", "stationID", "corporationID",
                "isLocatorAgent", "isInSpace", "careerID", "importantMission", "missionKind",
                "missionTypeLabel", "specialityID", "stationTypeID", "bloodlineID",
            ],
            [
                new PyList([
                    new PyInt(7004), new PyInt(8), new PyInt(24), new PyInt(3), PyNone.Instance,
                    new PyInt(1000036), PyBool.False, PyBool.True, new PyInt(17), PyBool.False,
                    new PyStr(""), new PyStr(""), new PyInt(18), new PyInt(1531), new PyInt(1),
                ]),
            ]), ResponseResult(sink.Packets[^1]));

        Assert.True(await dispatcher.DispatchAsync(Request(2, "GetMessagesForEpicArcMissions"), session));
        var nested = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyInt(430001), new PyStr("UI/Agent/EpicArc/Start")),
        ]);
        Assert.Equal(new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("start"), nested),
        ]), ResponseResult(sink.Packets[^1]));
    }

    private static PyValue Request(long callId, string method)
    {
        var body = new PyTuple([new PyInt(1), new PyStr(method), new PyTuple([]), PyNone.Instance]);
        var payload = new PyTuple([new PyTuple([new PyInt(0), new PySubStream(body)])]);
        var source = new MachoAddress(AddressKind.Client, CallId: new PyInt(callId), ClientId: new PyInt(42));
        var destination = new MachoAddress(AddressKind.Node, Service: "agentMgr", CallId: new PyInt(callId), NodeId: new PyInt(MachoMessage.ProxyNodeId));
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
