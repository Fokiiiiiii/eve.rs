using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public sealed class ActivityMgrArgumentHostRpcTests
{
    [Fact]
    public async Task Activity_mgr_wire_arguments_match_client_transport_and_python_values()
    {
        var registry = new ServiceRegistry();
        registry.Register(new ActivityMgrService());
        var dispatcher = new PacketDispatcher(registry);
        var capture = new PacketCapture();
        var session = new ClientSession(capture)
        {
            SessionId = 91,
            ClientId = 92,
            UserId = 93,
            CharacterId = 94,
        };
        var open = new SessionOpen(91, 92, 93, 0, "127.0.0.1", "EN", "GB");
        long callId = 0;

        async Task<PyValue?> Call(string method, params PyValue[] args)
        {
            capture.Sent.Clear();
            var kwargs = new PyDict([
                new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
            ]);
            var request = Packets.CallWithKwargs(open, ++callId, "activityMgr", method, new PyTuple(args), kwargs);
            Assert.True(await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(request)), session));
            var replies = capture.Sent.Where(packet =>
                MachoPacket.Decode(packet)?.Type == MachoMessage.CallRsp).ToArray();
            return Result(Assert.Single(replies));
        }

        Assert.Equal(PyBool.True, await Call("ActivitySeen", new PyLong(2_147_483_648)));
        Assert.Equal(PyBool.False, await Call("ActivitySeen", PyNone.Instance));
        Assert.Equal(new PyList([new PyLong(2_147_483_648)]), await Call("GetActivityBadgeStatuses"));
        var badgePackets = capture.Sent.Select(packet => Assert.IsType<MachoPacket>(MachoPacket.Decode(packet))).ToArray();
        Assert.Equal([MachoMessage.Notification, MachoMessage.CallRsp], badgePackets.Select(packet => packet.Type));
        Assert.Equal("OnSystemActivityUpdated", badgePackets[0].Dest.BroadcastId);

        Assert.Equal(PyBool.True, await Call("SetActivitiesPopState", PyBool.False));
        Assert.Equal(new PyInt(0), await Call("GetActivitiesPopState"));
        Assert.Equal(PyBool.True, await Call("SetActivitiesPopState", PyNone.Instance));
        Assert.Equal(new PyInt(1), await Call("GetActivitiesPopState"));
    }

    private static PyValue? Result(PyValue response)
    {
        var state = ((PyTuple)((PyObject)response).Args).Items;
        Assert.Equal(MachoMessage.CallRsp, ((PyInt)state[0]).Value);
        var payload = (PyTuple)state[4];
        return payload.Items[0] is PySubStream { Decoded: { } inner } ? inner : payload.Items[0];
    }

    private sealed class PacketCapture : IPacketSink
    {
        public List<PyValue> Sent { get; } = [];
        public void Send(PyValue packet) => Sent.Add(packet);
    }
}
