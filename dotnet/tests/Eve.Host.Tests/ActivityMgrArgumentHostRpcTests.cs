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

    [Fact]
    public async Task Missing_state_returns_wrapped_runtime_error_over_host_rpc()
    {
        var registry = new ServiceRegistry();
        registry.Register(new ActivityMgrService(new MissingActivityMgrStateStore()));
        var dispatcher = new PacketDispatcher(registry);
        var capture = new PacketCapture();
        var session = new ClientSession(capture)
        {
            SessionId = 95,
            ClientId = 96,
            UserId = 97,
            CharacterId = 0,
        };
        var open = new SessionOpen(95, 96, 97, 0, "127.0.0.1", "EN", "GB");
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        var request = Packets.CallWithKwargs(
            open, 1, "activityMgr", "GetActivitiesPopState", new PyTuple([]), kwargs);

        Assert.True(await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(request)), session));
        var error = Assert.Single(capture.Sent,
            packet => MachoPacket.Decode(packet)?.Type == MachoMessage.ErrorResponse);
        var response = ((PyTuple)((PyObject)error).Args).Items;
        var body = ((PyTuple)response[4]).Items;
        Assert.Equal(2, ((PyInt)body[1]).Value);
        var wrapped = ((PyTuple)body[2]).Items;
        var exception = Assert.IsType<PyObjectEx>(Assert.IsType<PySubStream>(wrapped[0]).Decoded);
        var header = Assert.IsType<PyTuple>(exception.Header).Items;
        Assert.Equal("exceptions.RuntimeError", Assert.IsType<PyToken>(header[0]).Value);
        var args = Assert.IsType<PyTuple>(header[1]).Items;
        var message = Assert.IsType<PyStr>(args[0]).Value;
        Assert.StartsWith("Unhandled error ", message);
        Assert.EndsWith(" in activityMgr.GetActivitiesPopState", message);
    }

    private static PyValue? Result(PyValue response)
    {
        var state = ((PyTuple)((PyObject)response).Args).Items;
        Assert.Equal(MachoMessage.CallRsp, ((PyInt)state[0]).Value);
        var payload = (PyTuple)state[4];
        return payload.Items[0] is PySubStream { Decoded: { } inner } ? inner : payload.Items[0];
    }

    private sealed class MissingActivityMgrStateStore : IActivityMgrStateStore
    {
        public ValueTask<ActivityMgrState?> ReadAsync(long characterId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ActivityMgrState?>(null);

        public ValueTask<ActivityMgrState?> UpdateAsync(
            long characterId,
            Func<ActivityMgrState, ActivityMgrState> update,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<ActivityMgrState?>(null);
    }

    private sealed class PacketCapture : IPacketSink
    {
        public List<PyValue> Sent { get; } = [];
        public void Send(PyValue packet) => Sent.Add(packet);
    }
}
