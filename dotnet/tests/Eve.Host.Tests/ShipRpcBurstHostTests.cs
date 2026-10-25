using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Ship_drone_launch_accepts_the_normal_500_entry_wire_burst()
    {
        var drones = new WireDrones();
        Start(
            configure: registry => registry.Register(new ShipService(null!, () => FixedNowMs, drones: drones)),
            sessionOpened: session => session.Set("stationid", new PyInt(600)));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        var requests = Enumerable.Range(0, 500)
            .Select(index => (PyValue)Py.Tuple(new PyLong(10_000 + index), new PyInt(1)))
            .ToArray();
        var bindArgs = Py.Tuple(
            Py.Tuple(new PyInt(600), new PyInt(15)),
            Py.Tuple(new PyStr("LaunchDrones"), Py.Tuple(new PyList(requests), new PyLong(7001), PyBool.False), kwargs));
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "ship", "MachoBindObject", bindArgs, kwargs))));

        var bound = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(
            Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));
        var result = Assert.IsType<PyDict>(bound.Items[1]);
        Assert.Equal(500, result.Entries.Count);
        Assert.Equal(500, drones.Requests.Count);
        Assert.Equal(new DroneRequest(10_499, 1), drones.Requests[^1]);
    }
}
