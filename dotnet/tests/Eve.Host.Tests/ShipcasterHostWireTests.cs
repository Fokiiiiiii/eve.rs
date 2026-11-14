using Eve.Macho;
using Eve.Marshal;
using System.Text;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Production_shipcaster_routes_refuse_when_dynamic_authority_is_missing()
    {
        var systems = new Eve.Universe.SolarSystemCatalog([]);
        Start(configure: registry =>
        {
            foreach (var service in Eve.Host.CharacterServicesComposition.CreateShipcasterServices(systems))
                registry.Register(service);
        });
        var (front, open) = await OpenFrontAsync();

        await AssertUnavailable("shipCasterLandingPadMgr", "GetAllLandingPads", 1);
        await AssertUnavailable("shipCasterLandingPadMgr", "GetFactionLandingPads", 2, new PyInt(500_001));
        await AssertUnavailable("shipCasterLauncherMgr", "GetFactionsWithShipcaster", 3);
        Assert.Empty(Assert.IsType<PyList>(await HostRpcAsync(front, open, 4,
            "shipCasterLandingPadMgr", "GetFactionLandingPads", new PyInt(500_099))).Items);
        Assert.Same(PyBool.False, await HostRpcAsync(front, open, 5,
            "starterShipcasterTravelMgr", "CanCharacterJump"));

        async Task AssertUnavailable(string service, string method, uint callId, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open, callId, service, method, new PyTuple(args), TransportKwargs()))));
            var send = Assert.IsType<Send>(await front.ReceiveAsync());
            var envelope = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(send.Payload)));
            Assert.Equal(MachoMessage.ErrorResponse, envelope.Type);
            Assert.Contains("CustomNotify", Encoding.ASCII.GetString(send.Payload.Span));
        }

        await AssertUnavailable("shipcasterTravelMgr", "CmdJumpThroughShipcaster", 6,
            new PyLong(77), new PyInt(30_000_001), new PyLong(90));
        await AssertUnavailable("starterShipcasterTravelMgr", "CmdJumpThroughStarterShipcaster", 7, new PyLong(77));
    }

    [Fact]
    public async Task Shipcaster_reads_return_client_wire_rows_through_host_dispatch()
    {
        var systems = new Eve.Universe.SolarSystemCatalog(
        [
            new Eve.Universe.SolarSystemInfo(30_000_001, "System A", 20_000_001, 10_000_001, 0.5, 500_001),
        ]);
        var pads = new[]
        {
            new Eve.Services.Space.ShipcasterLandingPad(9001, 30_000_001, 500_001, true, new PyLong(1234), false),
        };
        Start(configure: registry =>
        {
            registry.Register(new Eve.Services.Space.ShipcasterRpcService("shipCasterLandingPadMgr", systems, () => pads));
            registry.Register(new Eve.Services.Space.ShipcasterRpcService(
                "shipCasterLauncherMgr", systems, readFactionsWithShipcaster: () => [500_001]));
        });
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();

        async Task<PyValue?> Call(string service, string method, uint correlationId, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.Call(
                open, correlationId, service, method, args))));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            return Result(MarshalCodec.Decode(reply.Payload));
        }

        var all = Assert.IsType<PyList>(await Call("shipCasterLandingPadMgr", "GetAllLandingPads", 1));
        var byFaction = Assert.IsType<PyList>(await Call(
            "shipCasterLandingPadMgr", "GetFactionLandingPads", 2, new PyInt(500_001)));
        var factions = Assert.IsType<PyList>(await Call("shipCasterLauncherMgr", "GetFactionsWithShipcaster", 3));

        Assert.Single(all.Items);
        var row = Assert.IsType<PyObjectEx>(Assert.Single(all.Items));
        var packed = Assert.IsType<PyTuple>(row.Header).Items;
        Assert.Equal(new PyToken("shipcaster.landingPadData.LandingPadData"), packed[0]);
        Assert.Equal(new PyList([new PyLong(9001), new PyInt(30_000_001), new PyInt(500_001),
            PyBool.True, new PyLong(1234), PyBool.False]), packed[1]);
        Assert.Equal(all.Items, byFaction.Items);
        Assert.Equal([new PyInt(500_001)], factions.Items);
    }
}
