using Eve.Macho;
using Eve.Services;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Ship_fitting_info_and_dirt_timestamp_use_the_registered_host_routes()
    {
        var service = new ShipService(null!, () => FixedNowMs,
            getShipFittingInfo: (fittingId, _) => ValueTask.FromResult<ShipFittingInfo?>(
                fittingId == 7001 ? new ShipFittingInfo("Rifter fit", "res:/fit.png", 587, PyNone.Instance) : null));
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.Set("stationid", new PyInt(600)));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "ship", "GetShipFittingInfo", Py.Tuple(new PyInt(7001)), kwargs))));
        var fitting = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(
            Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));
        Assert.Equal(
            Py.Tuple(new PyStr("Rifter fit"), new PyStr("res:/fit.png"), new PyInt(587), PyNone.Instance),
            fitting);

        var bindArgs = Py.Tuple(
            Py.Tuple(new PyInt(600), new PyInt(15)),
            Py.Tuple(new PyStr("GetDirtTimestamp"), Py.Tuple(new PyInt(9001)), kwargs));
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 2, "ship", "MachoBindObject", bindArgs, kwargs))));
        var bound = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(
            Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));
        Assert.Equal(
            new PyLong(Py.FileTimeFromUnixMs(FixedNowMs)),
            bound.Items[1]);
    }
}
