using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Agent_mgr_GetAgents_rowset_reaches_the_client_through_host_registration()
    {
        var agents = new[]
        {
            new AgentMissionAgentData(
                7004, 8, 24, 3, null, 1000036, false, true, 17, false, "", "",
                18, 1531, 1, 30000165, 416, 4239, 20520),
            new AgentMissionAgentData(
                7002, 8, 24, 2, 600001, 1000036, true, false, null, false, "", "",
                null, 1531, null, 30000142, null, null, null),
        };
        var service = new AgentMissionService(
            (_, _) => ValueTask.FromResult<System.Text.Json.Nodes.JsonObject?>(null),
            (_, _, _) => ValueTask.FromResult<IReadOnlyDictionary<long, System.Text.Json.Nodes.JsonObject>>(
                new Dictionary<long, System.Text.Json.Nodes.JsonObject>()),
            (_, _) => ValueTask.FromResult<System.Text.Json.Nodes.JsonArray?>(null),
            rpcCallbacks: new AgentMissionRpcCallbacks(
                ReadAgents: _ => ValueTask.FromResult<IReadOnlyList<AgentMissionAgentData>>(agents)));

        Start(strictRpc: true, configure: registry => registry.Register(service));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();

        await front.SendAsync(new Packet(open.SessionId,
            MarshalCodec.Encode(Packets.Call(open, 1, "agentMgr", "GetAgents"))));
        var reply = Assert.IsType<Send>(await front.ReceiveAsync());

        var expected = Py.Rowset(
        [
            "agentID", "agentTypeID", "divisionID", "level", "stationID", "corporationID",
            "isLocatorAgent", "isInSpace", "careerID", "importantMission", "missionKind",
            "missionTypeLabel", "specialityID", "stationTypeID", "bloodlineID",
        ],
        [
            new PyList([
                new PyInt(7002), new PyInt(8), new PyInt(24), new PyInt(2), new PyInt(600001),
                new PyInt(1000036), PyBool.True, PyBool.False, PyNone.Instance, PyBool.False,
                new PyStr(""), new PyStr(""), PyNone.Instance, new PyInt(1531), PyNone.Instance,
            ]),
            new PyList([
                new PyInt(7004), new PyInt(8), new PyInt(24), new PyInt(3), PyNone.Instance,
                new PyInt(1000036), PyBool.False, PyBool.True, new PyInt(17), PyBool.False,
                new PyStr(""), new PyStr(""), new PyInt(18), new PyInt(1531), new PyInt(1),
            ]),
        ]);

        Assert.Equal(MarshalCodec.Decode(MarshalCodec.Encode(expected)),
            Result(MarshalCodec.Decode(reply.Payload)));
    }
}
