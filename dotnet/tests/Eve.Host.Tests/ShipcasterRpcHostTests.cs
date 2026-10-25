using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Space;
using Eve.Universe;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Shipcaster_read_rpcs_keep_client_shapes_through_host_dispatch()
    {
        const int systemId = 31_000_005;
        ShipcasterLandingPad[] pads =
        [
            new(90_002, systemId, 500_001, true, new PyLong(1234), false),
            new(90_001, systemId, 500_003, false, null, true),
        ];
        var systems = new SolarSystemCatalog(
        [
            new SolarSystemInfo(systemId, "System A", 20_000_001, 10_000_001, 0.5, 500_001),
        ]);
        var landingPads = new ShipcasterRpcService("shipCasterLandingPadMgr", systems, () => pads);
        var launchers = new ShipcasterRpcService(
            "shipCasterLauncherMgr",
            systems,
            () => pads,
            () => [500_003, 500_001, 500_003, 500_099]);
        Start(configure: registry =>
        {
            registry.Register(landingPads);
            registry.Register(launchers);
        });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);

        async Task<PyValue> Call(uint id, string service, string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, id, service, method, Py.Tuple(args), kwargs))));
            return Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload));
        }

        var allPads = Assert.IsType<PyList>(await Call(1, "shipCasterLandingPadMgr", "GetAllLandingPads"));
        Assert.Equal(2, allPads.Items.Count);
        var firstPad = Assert.IsType<PyObjectEx>(allPads.Items[0]);
        Assert.Equal(ObjectExKind.Ex1, firstPad.Kind);
        Assert.Equal("shipcaster.landingPadData.LandingPadData",
            JsSemantics.StringValue(Assert.IsType<PyToken>(Assert.IsType<PyTuple>(firstPad.Args).Items[0])));
        Assert.Equal(new PyList([new PyLong(90_002), new PyInt(systemId), new PyInt(500_001),
            PyBool.True, new PyLong(1234), PyBool.False]),
            Assert.IsType<PyList>(Assert.IsType<PyTuple>(firstPad.Args).Items[1]));

        var factionPads = Assert.IsType<PyList>(await Call(2, "shipCasterLandingPadMgr", "GetFactionLandingPads", new PyInt(500_003)));
        Assert.Single(factionPads.Items);
        var factions = Assert.IsType<PyList>(await Call(3, "shipCasterLauncherMgr", "GetFactionsWithShipcaster"));
        Assert.Equal([new PyInt(500_001), new PyInt(500_003)], factions.Items);
    }
}
