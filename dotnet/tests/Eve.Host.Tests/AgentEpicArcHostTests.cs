using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Agent_mgr_epic_arc_messages_preserve_the_nested_get_lookup_shape_through_host_rpc()
    {
        var messages = System.Text.Json.Nodes.JsonNode.Parse(
            """{"start":{"430001":"UI/Agent/EpicArc/Start","430002":123}}""")!.AsObject();
        var service = new AgentMissionService(
            (_, _) => ValueTask.FromResult<System.Text.Json.Nodes.JsonObject?>(null),
            (_, _, _) => ValueTask.FromResult<IReadOnlyDictionary<long, System.Text.Json.Nodes.JsonObject>>(
                new Dictionary<long, System.Text.Json.Nodes.JsonObject>()),
            (_, _) => ValueTask.FromResult<System.Text.Json.Nodes.JsonArray?>(null),
            rpcCallbacks: new AgentMissionRpcCallbacks(
                ReadEpicArcMessages: _ => ValueTask.FromResult<System.Text.Json.Nodes.JsonObject?>(messages)));

        Start(strictRpc: true, configure: registry => registry.Register(service));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();

        await front.SendAsync(new Packet(open.SessionId,
            MarshalCodec.Encode(Packets.Call(open, 1, "agentMgr", "GetMessagesForEpicArcMissions"))));
        var reply = Assert.IsType<Send>(await front.ReceiveAsync());

        var expected = new PyDict([
            new KeyValuePair<PyValue, PyValue>(
                new PyStr("start"),
                new PyDict([
                    new KeyValuePair<PyValue, PyValue>(new PyInt(430001), new PyStr("UI/Agent/EpicArc/Start")),
                    new KeyValuePair<PyValue, PyValue>(new PyInt(430002), new PyInt(123)),
                ])),
        ]);

        Assert.Equal(MarshalCodec.Decode(MarshalCodec.Encode(expected)),
            Result(MarshalCodec.Decode(reply.Payload)));
    }
}
