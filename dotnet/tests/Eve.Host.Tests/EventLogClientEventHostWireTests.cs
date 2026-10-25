using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Client_event_is_logged_through_host_registration_with_transport_keywords()
    {
        var records = new List<string>();
        var service = new EventLogRpcService(records.Add);
        Start(configure: registry => registry.Register(service));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        var args = new PyTuple([
            new PyStr("flightControls"),
            new PyList([new PyInt(3), new PyInt(4)]),
            new PyStr("Counter"),
            new PyInt(30_100_041),
            new PyStr("Rifter"),
        ]);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "eventLog", "LogClientEvent", args, kwargs))));

        var reply = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.IsType<PyNone>(Result(MarshalCodec.Decode(reply.Payload)));
        Assert.Equal(["LogClientEvent category=flightControls event=Counter"], records);
    }
}
