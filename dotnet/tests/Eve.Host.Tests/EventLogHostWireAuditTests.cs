using System.Text;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Client_stats_and_requested_disconnect_use_the_registered_host_route()
    {
        var records = new List<string>();
        var service = new EventLogRpcService(records.Add);
        Start(configure: registry => registry.Register(service));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        var sessionId = unchecked((long)open.SessionId);

        PyDict TransportKwargs() => new([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        PyDict ShutdownKwargs() => new([
            new KeyValuePair<PyValue, PyValue>(new PyStr("isShutdown"), PyBool.True),
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        static int Message(Send send) =>
            Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(send.Payload))).Type;
        async Task SendCall(long callId, string method, PyTuple args, PyDict kwargs)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, callId, "eventLog", method, args, kwargs))));
            var response = Assert.IsType<Send>(await front.ReceiveAsync());
            Assert.Equal(MachoMessage.CallRsp, Message(response));
            Assert.IsType<PyNone>(Result(MarshalCodec.Decode(response.Payload)));
        }

        await SendCall(1, "LogClientStats", new PyTuple([
            new PyLong(sessionId), new PyInt(30_100_041),
            new PyList([new PyList([new PyStr("periodic")])]), new PyList([]),
        ]), TransportKwargs());
        await SendCall(2, "LogClientStats", new PyTuple([
            new PyLong(sessionId), new PyLong(60_000_000_001L),
            new PyList([new PyList([new PyStr("structure")])]), new PyList([]),
        ]), TransportKwargs());
        await SendCall(3, "LogClientStats", new PyTuple([
            new PyLong(sessionId), PyNone.Instance,
            new PyList([new PyList([new PyStr("shutdown")])]),
        ]), ShutdownKwargs());
        await SendCall(4, "LogPlayerRequestedDisconnect", new PyTuple([]), TransportKwargs());

        Assert.Equal([
            "LogClientStats location=30100041 rows=1",
            "LogClientStats location=60000000001 rows=1",
            "LogClientStats location= rows=1",
            "LogPlayerRequestedDisconnect",
        ], records);
    }

    [Fact]
    public async Task Invalid_event_log_calls_return_the_exact_user_error_over_the_host_wire()
    {
        var records = new List<string>();
        var service = new EventLogRpcService(records.Add);
        Start(configure: registry => registry.Register(service));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        var sessionId = unchecked((long)open.SessionId);

        PyDict TransportKwargs() => new([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        PyDict ShutdownFalseKwargs() => new([
            new KeyValuePair<PyValue, PyValue>(new PyStr("isShutdown"), PyBool.False),
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        static int Message(Send send) =>
            Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(send.Payload))).Type;
        async Task<Send> SendCall(long callId, string method, PyTuple args, PyDict kwargs)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, callId, "eventLog", method, args, kwargs))));
            return Assert.IsType<Send>(await front.ReceiveAsync());
        }
        static void AssertUserError(Send response, string error)
        {
            Assert.Equal(MachoMessage.ErrorResponse, Message(response));
            Assert.Contains(error, Encoding.Latin1.GetString(response.Payload.Span));
        }

        AssertUserError(await SendCall(1, "LogClientStats", new PyTuple([
            new PyLong(sessionId), PyNone.Instance, new PyList([]),
        ]), ShutdownFalseKwargs()), "CLIENT_STATS_LOCATION_INVALID");
        AssertUserError(await SendCall(2, "LogClientStats", new PyTuple([
            new PyLong(sessionId + 1), new PyInt(30_100_041), new PyList([]),
        ]), TransportKwargs()), "CLIENT_STATS_SESSION_INVALID");
        AssertUserError(await SendCall(3, "LogClientEvent", new PyTuple([]), TransportKwargs()), "CLIENT_EVENT_ARGUMENTS_INVALID");
        Assert.Empty(records);
    }
}
