using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;
using Eve.Universe;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Rpc_coverage_02489_02490_02768_02769_02770_03003_uses_registered_host_routes()
    {
        var solarSystems = new SolarSystemCatalog([]);
        Start(configure: registry =>
        {
            registry.Register(new ShipcasterRpcService("shipcasterTravelMgr", solarSystems));
            registry.Register(new ShipcasterRpcService("starterShipcasterTravelMgr", solarSystems));
        });
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();

        async Task<Send> Invoke(string service, string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.Call(open, 1, service, method, args))));
            while (true)
            {
                var reply = Assert.IsType<Send>(await front.ReceiveAsync());
                var envelope = Assert.IsType<MachoPacket>(
                    MachoPacket.Decode(MarshalCodec.Decode(reply.Payload)));
                if (envelope.Type is MachoMessage.CallRsp or MachoMessage.ErrorResponse)
                    return reply;
            }
        }

        static MachoPacket Envelope(Send reply) =>
            Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(reply.Payload)));

        var stance = await Invoke("shipStanceMgr", "SetShipStance", new PyLong(74_000_001), new PyInt(3));
        Assert.Equal(MachoMessage.ErrorResponse, Envelope(stance).Type);

        var canJump = await Invoke("starterShipcasterTravelMgr", "CanCharacterJump");
        Assert.Equal(MachoMessage.CallRsp, Envelope(canJump).Type);
        Assert.Same(PyBool.False, Result(MarshalCodec.Decode(canJump.Payload)));

        var shipcasterJump = await Invoke(
            "shipcasterTravelMgr", "CmdJumpThroughShipcaster",
            new PyLong(77), new PyLong(30_000_001), new PyLong(90));
        Assert.Equal(MachoMessage.ErrorResponse, Envelope(shipcasterJump).Type);

        var starterJump = await Invoke(
            "starterShipcasterTravelMgr", "CmdJumpThroughStarterShipcaster", new PyLong(77));
        Assert.Equal(MachoMessage.ErrorResponse, Envelope(starterJump).Type);

        var voidSpaceExit = await Invoke("voidSpaceMgr", "VoidSpaceEndGateActivation", new PyLong(77));
        Assert.Equal(MachoMessage.ErrorResponse, Envelope(voidSpaceExit).Type);
    }
}
