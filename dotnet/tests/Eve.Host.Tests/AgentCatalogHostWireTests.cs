using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Eve.Host;
using Eve.Identity;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public sealed class AgentCatalogHostWireTests
{
    [Fact]
    public async Task Agent_rowset_and_epic_arc_message_mapping_are_returned_through_registered_host_wire()
    {
        const long agentId = 70000001;
        const long missionId = 43000001;
        const long messageId = 60000001;
        const long nowMs = 1_790_646_377_000;
        var agent = new AgentMissionAgentData(
            agentId, 2, 22, 3, 60000001, 1000001, false, false, null, false,
            "courier", "UI/Agents/StandardMission/Courier", null, 1531, null, 30000001, null, null, null);
        var service = new AgentMissionService(
            (_, _) => ValueTask.FromResult<JsonObject?>(null),
            (_, _, _) => ValueTask.FromResult<IReadOnlyDictionary<long, JsonObject>>(new Dictionary<long, JsonObject>()),
            (_, _) => ValueTask.FromResult<JsonArray?>(null),
            rpcCallbacks: new AgentMissionRpcCallbacks(
                ReadAgents: _ => ValueTask.FromResult<IReadOnlyList<AgentMissionAgentData>>([agent]),
                ReadEpicArcMessages: _ => ValueTask.FromResult<JsonObject?>(new JsonObject
                {
                    ["messages.epicMission.journalText.chapterTitle"] = new JsonObject
                    {
                        [missionId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = messageId,
                    },
                })));

        var registry = new ServiceRegistry();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => nowMs);
        using var cancellation = new CancellationTokenSource();
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(16);
        var server = new HostServer(
            new HostOptions { Log = _ => { } },
            dispatcher,
            new InMemoryAccountDirectory(new AccountOptions { AutoCreate = true }),
            () => MarshalCodec.Encode(new PyDict([])));
        var serving = server.ServeAsync(listener, cancellation.Token);
        using var front = await TestFront.ConnectAsync((IPEndPoint)listener.LocalEndPoint!);
        try
        {
            await front.SendAsync(new Hello(HostCodec.ProtocolVersion, 0xFFAA));
            Assert.Equal(new HelloAck(HostCodec.ProtocolVersion), await front.ReceiveAsync());
            Assert.IsType<ClusterConfig>(await front.ReceiveAsync());
            var accepted = Assert.IsType<Accepted>((await front.AuthenticateAsync("agent-catalog-wire", [1], 1)).Outcome);
            var session = new SessionOpen(1_790_646_377_000UL * 15, (accepted.UserId * 1_000_000UL) + 0xFFAA,
                accepted.UserId, accepted.Role, "127.0.0.1", "EN", accepted.CountryCode);
            await front.SendAsync(session);
            Assert.IsType<Send>(await front.ReceiveAsync());

            async Task<PyValue> CallAgentMgr(uint callId, string method)
            {
                await front.SendAsync(new Packet(session.SessionId,
                    MarshalCodec.Encode(Packets.Call(session, callId, "agentMgr", method))));
                return CallResult(await front.ReceiveAsync());
            }

            var rowset = Assert.IsType<PyObject>(await CallAgentMgr(1, "GetAgents"));
            Assert.Equal("eve.common.script.sys.rowset.Rowset", JsSemantics.StringValue(rowset.Name));
            var fields = Assert.IsType<PyDict>(rowset.Args).Entries
                .ToDictionary(entry => JsSemantics.StringValue(entry.Key)!, entry => entry.Value);
            var headers = Assert.IsType<PyList>(fields["header"]).Items.Select(JsSemantics.StringValue).ToArray();
            Assert.Contains("agentID", headers);
            Assert.Contains("divisionID", headers);
            var agentRow = Assert.IsType<PyList>(Assert.Single(Assert.IsType<PyList>(fields["lines"]).Items));
            Assert.Equal(new PyInt(agentId), agentRow.Items[0]);
            Assert.Equal(new PyInt(22), agentRow.Items[2]);

            var messages = Assert.IsType<PyDict>(await CallAgentMgr(2, "GetMessagesForEpicArcMissions"));
            var chapterMessages = Assert.Single(messages.Entries,
                entry => JsSemantics.StringValue(entry.Key) == "messages.epicMission.journalText.chapterTitle");
            var byMission = Assert.IsType<PyDict>(chapterMessages.Value);
            Assert.Contains(byMission.Entries,
                entry => entry.Key == new PyInt(missionId) && entry.Value == new PyInt(messageId));
            Assert.DoesNotContain(byMission.Entries, entry => entry.Key == new PyInt(missionId + 1));
        }
        finally
        {
            front.Dispose();
            await cancellation.CancelAsync();
            listener.Dispose();
            await serving;
        }
    }

    private static PyValue CallResult(HostMessage message)
    {
        var send = Assert.IsType<Send>(message);
        var response = Assert.IsType<PyObject>(MarshalCodec.Decode(send.Payload));
        var fields = Assert.IsType<PyTuple>(response.Args).Items;
        Assert.Equal(MachoMessage.CallRsp, ((PyInt)fields[0]).Value);
        var payload = Assert.IsType<PyTuple>(fields[4]);
        return payload.Items[0] is PySubStream { Decoded: { } inner } ? inner : payload.Items[0];
    }
}
