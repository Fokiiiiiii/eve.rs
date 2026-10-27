using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Ship_fitting_info_route_is_available_without_space_sessions()
    {
        var service = new ShipFittingInfoOnlyService(new ShipService(null!, () => FixedNowMs,
            getShipFittingInfo: (_, _) => ValueTask.FromResult<ShipFittingInfo?>(
                new ShipFittingInfo("Rifter fit", "res:/fit.png", 587, new PyStr("Platinum")))));
        Assert.True(service.HasMethod(nameof(ShipService.GetShipFittingInfo)));
        Assert.False(service.HasMethod("Undock"));
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

        Assert.Equal(4, fitting.Items.Count);
        Assert.IsType<PyBytes>(fitting.Items[0]);
        Assert.Equal("Rifter fit", JsSemantics.StringValue(fitting.Items[0]));
        Assert.IsType<PyBytes>(fitting.Items[1]);
        Assert.Equal("res:/fit.png", JsSemantics.StringValue(fitting.Items[1]));
        Assert.Equal(new PyInt(587), fitting.Items[2]);
        Assert.IsType<PyBytes>(fitting.Items[3]);
        Assert.Equal("Platinum", JsSemantics.StringValue(fitting.Items[3]));
    }
}
