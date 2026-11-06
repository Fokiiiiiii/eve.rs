using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public sealed class ActivityMgrHostRpcTests
{
    [Fact]
    public async Task Activity_mgr_calls_route_through_host_wire_and_preserve_character_state()
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
        var otherSession = new ClientSession(new PacketCapture()) { CharacterId = 95 };
        var open = new SessionOpen(91, 92, 93, 0, "127.0.0.1", "EN", "GB");

        async Task<PyValue?> Call(long callId, ClientSession target, string method, params PyValue[] args)
        {
            var targetCapture = (PacketCapture)target.Sink!;
            targetCapture.Sent.Clear();
            var request = Packets.Call(open, callId, "activityMgr", method, args);
            var decodedRequest = MarshalCodec.Decode(MarshalCodec.Encode(request));
            Assert.True(await dispatcher.DispatchAsync(decodedRequest, target));
            var replies = targetCapture.Sent.Where(packet =>
                MachoPacket.Decode(packet)?.Type == MachoMessage.CallRsp).ToArray();
            return Result(Assert.Single(replies));
        }

        Assert.Equal(new PyInt(1), await Call(1, session, "GetActivitiesPopState"));
        Assert.Equal(PyBool.True, await Call(2, session, "SetActivitiesPopState", new PyInt(0)));
        Assert.Equal(new PyInt(0), await Call(3, session, "GetActivitiesPopState"));
        Assert.Equal(new PyInt(1), await Call(4, otherSession, "GetActivitiesPopState"));

        Assert.Equal(PyBool.True, await Call(5, session, "ActivitySeen", new PyLong(2_147_483_648)));
        capture.Sent.Clear();
        var badges = Assert.IsType<PyList>(await Call(6, session, "GetActivityBadgeStatuses"));
        Assert.Equal(new PyLong(2_147_483_648), badges.Items.Single());
        var packets = capture.Sent.Select(packet => Assert.IsType<MachoPacket>(MachoPacket.Decode(packet))).ToArray();
        Assert.Equal(2, packets.Length);
        Assert.Equal(MachoMessage.Notification, packets[0].Type);
        Assert.Equal("OnSystemActivityUpdated", packets[0].Dest.BroadcastId);
        Assert.Equal("None", packets[0].Dest.IdType);
        var envelope = Assert.IsType<PyTuple>(packets[0].Payload).Items;
        var notificationParts = Assert.IsType<PyTuple>(envelope[0]).Items;
        var notification = Assert.IsType<PyTuple>(MarshalCodec.Decode(
            Assert.IsType<PyBuffer>(notificationParts[1]).Value)).Items;
        Assert.Equal(new PyInt(0), notification[0]);
        var eventParts = Assert.IsType<PyTuple>(notification[1]).Items;
        Assert.Equal(new PyInt(1), eventParts[0]);
        Assert.Empty(Assert.IsType<PyTuple>(eventParts[1]).Items);
        Assert.Equal(MachoMessage.CallRsp, packets[1].Type);
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
