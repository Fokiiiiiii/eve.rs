using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Client_transport_macho_version_is_not_an_application_keyword_for_core_read_calls()
    {
        var airNpe = new AirNpeService(
            () => "tutorial",
            (_, _) => Task.FromResult<AirNpeCharacterState?>(null),
            (_, _, _) => Task.FromResult<int?>(null));
        Start(configure: registry => registry.Register(airNpe), strictRpc: true);
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);

        async Task<PyValue> RawCall(long callId, string service, string method, PyDict callKwargs)
        {
            await front.SendAsync(new Packet(open.SessionId,
                MarshalCodec.Encode(Packets.CallWithKwargs(
                    open, callId, service, method, new PyTuple([]), callKwargs))));
            return MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        }

        async Task<PyValue?> Call(long callId, string service, string method) =>
            Result(await RawCall(callId, service, method, kwargs));

        Assert.Equal(PyBool.True, await Call(1, "air_npe", "is_air_npe_enabled"));
        Assert.Same(PyNone.Instance, await Call(2, "pingService", "Ping"));
        Assert.Equal(new PyInt(1), await Call(3, "subscriptionMgr", "GetCloneGrade"));

        var malformedKwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("unexpected"), PyBool.True),
        ]);
        var malformedResponse = await RawCall(4, "pingService", "Ping", malformedKwargs);
        var errorState = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(malformedResponse).Args).Items;
        Assert.Equal((int)MachoMessage.ErrorResponse, Assert.IsType<PyInt>(errorState[0]).Value);
    }
}
