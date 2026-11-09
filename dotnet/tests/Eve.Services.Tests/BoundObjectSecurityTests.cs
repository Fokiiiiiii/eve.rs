using System.Text;
using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Tests;

public sealed class BoundObjectSecurityTests
{
    private const string BoundOid = "N=77:9001";

    private sealed class Capture : IPacketSink
    {
        public List<byte[]> Sent { get; } = [];

        public void Send(PyValue packet) => Sent.Add(MarshalCodec.Encode(packet));
    }

    private sealed class BoundService : IService
    {
        public string Name => "boundSecuritySvc";

        public int BoundCalls { get; private set; }

        public bool HasMethod(string method) => method is "MachoBindObject" or "ReadBound";

        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            if (call.Method == "MachoBindObject")
            {
                var registration = new PySubStruct(new PySubStream(
                    new PyTuple([new PyStr(BoundOid), new PyLong(100)])));
                return ValueTask.FromResult<PyValue>(new PyTuple([registration, PyNone.Instance]));
            }

            BoundCalls++;
            return ValueTask.FromResult<PyValue>(new PyStr(BoundOid));
        }
    }

    private sealed class AfterResponseService : IService
    {
        public string Name => "afterResponseSvc";

        public bool HasMethod(string method) => method == "Run";

        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            call.Session.AfterResponse(() => throw new InvalidOperationException("after response failed"));
            call.Session.AfterResponse(() => call.Session.SendNotification(
                "OnAfterResponse", "charid", new PyInt(42)));
            return ValueTask.FromResult<PyValue>(new PyInt(7));
        }
    }

    [Fact]
    public async Task A_bound_oid_call_is_rejected_for_a_session_that_never_received_it()
    {
        var service = new BoundService();
        var registry = new ServiceRegistry();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry);
        var ownerPackets = new Capture();
        var foreignPackets = new Capture();
        var owner = new ClientSession(ownerPackets) { SessionId = 1 };
        var foreign = new ClientSession(foreignPackets) { SessionId = 2 };

        await dispatcher.DispatchAsync(CallRequest("boundSecuritySvc", "MachoBindObject"), owner);
        await dispatcher.DispatchAsync(BoundCall(BoundOid, "ReadBound"), owner);
        await dispatcher.DispatchAsync(BoundCall(BoundOid, "ReadBound"), foreign);

        Assert.Equal(MachoMessage.CallRsp, Packet(ownerPackets.Sent[1]).Type);
        var denied = Packet(Assert.Single(foreignPackets.Sent));
        Assert.Equal(MachoMessage.ErrorResponse, denied.Type);
        Assert.Contains("UnMachoDestination", Encoding.Latin1.GetString(foreignPackets.Sent[0]));
        Assert.Equal(1, service.BoundCalls);
    }

    [Fact]
    public async Task An_after_response_callback_failure_does_not_change_the_answer_or_stop_later_notifications()
    {
        var registry = new ServiceRegistry();
        registry.Register(new AfterResponseService());
        var dispatcher = new PacketDispatcher(registry);
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 3, CharacterId = 42 };
        var problems = new List<(string Message, Exception? Error)>();
        dispatcher.OnProblem = (message, error) => problems.Add((message, error));

        await dispatcher.DispatchAsync(CallRequest("afterResponseSvc", "Run"), session);

        Assert.Equal(
            [MachoMessage.CallRsp, MachoMessage.Notification],
            capture.Sent.Select(bytes => Packet(bytes).Type));
        Assert.Equal(new PyInt(7), Result(capture.Sent[0]));
        Assert.Contains("after-response", Assert.Single(problems).Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<InvalidOperationException>(problems[0].Error);
        var notification = Packet(capture.Sent[1]);
        Assert.Equal("OnAfterResponse", notification.Dest.BroadcastId);
    }

    private static PyValue CallRequest(string service, string method)
    {
        var source = new PyTuple([new PyInt(MachoAddress.TypeClient), new PyLong(1), new PyInt(1), PyNone.Instance]);
        var body = new PyTuple([new PyInt(1), new PyStr(method), new PyTuple([]), PyNone.Instance]);
        var payload = new PyTuple([new PyTuple([new PyInt(0), new PySubStream(body)])]);
        var head = new PyValue[]
        {
            new PyInt(MachoMessage.CallReq), source, MachoAddress.Any(service).Encode(), new PyInt(42), payload, PyNone.Instance,
        };
        return new PyObject(
            new PyStr("carbon.common.script.net.machoNetPacket.CallReq"),
            new PyTuple([.. head, .. Enumerable.Repeat<PyValue>(PyNone.Instance, 14 - head.Length)]));
    }

    private static PyValue BoundCall(string oid, string method)
    {
        var source = new PyTuple([new PyInt(MachoAddress.TypeClient), new PyLong(2), new PyInt(2), PyNone.Instance]);
        var body = new PyTuple([new PyStr(oid), new PyStr(method), new PyTuple([]), PyNone.Instance]);
        var payload = new PyTuple([new PyTuple([new PyInt(0), new PySubStream(body)])]);
        var head = new PyValue[]
        {
            new PyInt(MachoMessage.CallReq), source, MachoAddress.Any("").Encode(), new PyInt(42), payload, PyNone.Instance,
        };
        return new PyObject(
            new PyStr("carbon.common.script.net.machoNetPacket.CallReq"),
            new PyTuple([.. head, .. Enumerable.Repeat<PyValue>(PyNone.Instance, 14 - head.Length)]));
    }

    private static MachoPacket Packet(byte[] bytes) =>
        MachoPacket.Decode(MarshalCodec.Decode(bytes)) ?? throw new InvalidOperationException("the packet did not decode");

    private static PyValue Result(byte[] bytes)
    {
        var packet = Packet(bytes);
        var result = Assert.IsType<PyTuple>(packet.Payload).Items[0];
        return result is PySubStream { Decoded: { } decoded } ? decoded : result;
    }
}
