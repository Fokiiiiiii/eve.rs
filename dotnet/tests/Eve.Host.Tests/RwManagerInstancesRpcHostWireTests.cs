using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Resource_wars_instance_lookup_returns_false_in_the_empty_world()
    {
        Start(
            configure: registry => registry.Register(new RWManagerService()),
            sessionOpened: session => session.Set("solarsystemid2", new PyLong(30_000_142)));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            open,
            1,
            "RWManager",
            "solarsystem_contains_rw_instances",
            new PyTuple([]),
            MachoVersion()))));
        var reply = Assert.IsType<Send>(await front.ReceiveAsync());
        var decoded = MarshalCodec.Decode(reply.Payload);
        var envelope = Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded));
        Assert.Equal(MachoMessage.CallRsp, envelope.Type);
        Assert.Equal(PyBool.False, Result(decoded));
    }
}
