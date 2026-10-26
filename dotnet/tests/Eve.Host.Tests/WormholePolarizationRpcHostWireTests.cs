using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Wormhole_polarization_returns_client_timer_fields_through_registered_host()
    {
        long readEndpointId = 0;
        long readCharacterId = 0;
        var service = new WormholeMgrService(
            spaceSessions: null,
            readPolarization: (endpointId, characterId, _) =>
            {
                readEndpointId = endpointId;
                readCharacterId = characterId;
                return ValueTask.FromResult<WormholePolarization?>(
                    endpointId == 654 ? new WormholePolarization(1_900_000_000_000, 300) : null);
            });
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.CharacterId = 9001);

        using var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        async Task<PyValue> Call(long callId, long endpointId)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open,
                callId,
                "wormholeMgr",
                "GetWormholePolarization",
                new PyTuple([new PyLong(endpointId)]),
                new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))])))));
            return Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload))!;
        }

        Assert.Equal(
            PyList.Of(new PyLong(Py.FileTimeFromUnixMs(1_900_000_000_000)), new PyInt(300)),
            await Call(1, 654));
        Assert.Equal(654, readEndpointId);
        Assert.Equal(9001, readCharacterId);
        Assert.Equal(PyNone.Instance, await Call(2, 655));
    }
}
