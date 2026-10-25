using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Space;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Structure_cyno_jump_uses_the_registered_host_route_with_transport_macho_version()
    {
        var jumpInvoked = false;
        var travel = new StructureBeaconTravel(
            (_, _) => ValueTask.FromResult<StructureBeaconInfo?>(
                new(StructureBeaconTravel.PharoluxCynoBeaconType, 30_000_142, true)),
            (_, _) => ValueTask.FromResult(false),
            (_, _, _, _) => ValueTask.FromResult(true),
            (_, _, _, _, _, _) =>
            {
                jumpInvoked = true;
                return Task.FromResult<PyValue>(PyBool.True);
            });
        Start(
            configure: registry => registry.Register(
                new StructureTravelRpcService("structureCynoBeaconMgr", travel.TravelAsync)),
            sessionOpened: session =>
            {
                session.Set("charid", new PyInt(90_001));
                session.Set("solarsystemid2", new PyInt(30_000_001));
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            open,
            1,
            "structureCynoBeaconMgr",
            "CmdJumpToStructureBeacon",
            new PyTuple([new PyLong(90_002), new PyInt(30_000_142)]),
            kwargs))));

        var reply = Assert.IsType<Send>(await front.ReceiveAsync());
        var response = MarshalCodec.Decode(reply.Payload);
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(response)).Type);
        Assert.Same(PyBool.True, Result(response));
        Assert.True(jumpInvoked);
    }
}