using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;
using Eve.Universe;

namespace Eve.Services.Space.Tests;

public sealed class ShipcasterRpcTests
{
    private static readonly SolarSystemCatalog Systems = new(
    [
        new SolarSystemInfo(30_000_001, "System A", 20_000_001, 10_000_001, 0.5, 500_001),
        new SolarSystemInfo(30_000_002, "System B", 20_000_002, 10_000_002, 0.7, 500_003),
    ]);

    [Theory]
    [InlineData("shipCasterLandingPadMgr", "GetAllLandingPads")]
    [InlineData("shipCasterLandingPadMgr", "GetFactionLandingPads")]
    [InlineData("shipCasterLauncherMgr", "GetFactionsWithShipcaster")]
    [InlineData("shipcasterTravelMgr", "CmdJumpThroughShipcaster")]
    [InlineData("starterShipcasterTravelMgr", "CanCharacterJump")]
    [InlineData("starterShipcasterTravelMgr", "CmdJumpThroughStarterShipcaster")]
    public void All_requested_methods_are_registered(string serviceName, string method)
    {
        var service = new ShipcasterRpcService(serviceName, Systems);

        Assert.True(service.HasMethod(method));
    }

    [Fact]
    public async Task Landing_pads_use_the_reference_object_ex_shape_and_sde_systems()
    {
        ShipcasterLandingPad[] pads =
        [
            new(9002, 30_000_002, 500_001, true, new PyLong(1234), false),
            new(9001, 30_000_001, 500_003, false, null, true),
            new(9003, 30_000_001, 500_099, true),
            new(9004, 30_000_099, 500_001, true),
        ];
        var service = new ShipcasterRpcService("shipCasterLandingPadMgr", Systems, () => pads);

        var result = Assert.IsType<PyList>(await service.CallAsync(Call("GetAllLandingPads")));

        Assert.Equal(
        [
            LandingPadWire(9002, 30_000_002, 500_001, true, new PyLong(1234), false),
            LandingPadWire(9001, 30_000_001, 500_003, false, PyNone.Instance, true),
        ],
        result.Items);
    }

    [Fact]
    public async Task Faction_queries_filter_pads_and_return_sorted_distinct_factions()
    {
        ShipcasterLandingPad[] pads =
        [
            new(9002, 30_000_002, 500_001, true),
            new(9001, 30_000_001, 500_003, false),
            new(9005, 30_000_001, 500_001, false),
        ];
        var landingPads = new ShipcasterRpcService("shipCasterLandingPadMgr", Systems, () => pads);
        var launchers = new ShipcasterRpcService(
            "shipCasterLauncherMgr",
            Systems,
            () => pads,
            () => [500_004, 500_003, 500_001, 500_003, 500_099]);

        var factionPads = Assert.IsType<PyList>(await landingPads.CallAsync(Call("GetFactionLandingPads", new PyInt(500_001))));
        var unknownFactionPads = Assert.IsType<PyList>(await landingPads.CallAsync(Call("GetFactionLandingPads", new PyInt(500_099))));
        var factions = Assert.IsType<PyList>(await launchers.CallAsync(Call("GetFactionsWithShipcaster")));

        Assert.Equal([LandingPadWire(9005, 30_000_001, 500_001, false, PyNone.Instance, false), LandingPadWire(9002, 30_000_002, 500_001, true, PyNone.Instance, false)], factionPads.Items);
        Assert.Equal(
        [
            LandingPadWire(9005, 30_000_001, 500_001, false, PyNone.Instance, false),
            LandingPadWire(9002, 30_000_002, 500_001, true, PyNone.Instance, false),
            LandingPadWire(9001, 30_000_001, 500_003, false, PyNone.Instance, false),
        ], unknownFactionPads.Items);
        Assert.Equal([new PyInt(500_001), new PyInt(500_003), new PyInt(500_004)], factions.Items);
    }

    [Theory]
    [InlineData("shipcasterTravelMgr", "CmdJumpThroughShipcaster")]
    [InlineData("starterShipcasterTravelMgr", "CmdJumpThroughStarterShipcaster")]
    public async Task Travel_rpcs_forward_the_original_call(string serviceName, string method)
    {
        ServiceCall? forwarded = null;
        var expected = PyNone.Instance;
        var service = new ShipcasterRpcService(serviceName, Systems, travel: call =>
        {
            forwarded = call;
            return ValueTask.FromResult<PyValue>(expected);
        });
        var call = TravelCall(method);

        Assert.Same(expected, await service.CallAsync(call));
        Assert.Same(call, forwarded);
    }

    [Theory]
    [InlineData("shipcasterTravelMgr", "CmdJumpThroughShipcaster")]
    [InlineData("starterShipcasterTravelMgr", "CmdJumpThroughStarterShipcaster")]
    public async Task Travel_rpcs_report_missing_backends(string serviceName, string method)
    {
        var service = new ShipcasterRpcService(serviceName, Systems);

        await Assert.ThrowsAsync<MachoWrappedException>(async () => await service.CallAsync(TravelCall(method)));
    }

    [Fact]
    public async Task Starter_jump_check_returns_a_boolean_for_wired_and_unseeded_characters()
    {
        var session = new ClientSession { CharacterId = 42 };
        var service = new ShipcasterRpcService("starterShipcasterTravelMgr", Systems, canCharacterJump: call => call.Session.CharacterId == 42);
        var defaultService = new ShipcasterRpcService("starterShipcasterTravelMgr", Systems);

        Assert.Equal(PyBool.True, await service.CallAsync(Call("CanCharacterJump", session: session)));
        Assert.Equal(PyBool.False, await defaultService.CallAsync(Call("CanCharacterJump", session: session)));
    }

    [Fact]
    public async Task Unknown_methods_keep_the_none_fallback()
    {
        var service = new ShipcasterRpcService("shipcasterTravelMgr", Systems);

        Assert.Same(PyNone.Instance, await service.CallAsync(Call("UnknownShipcasterMethod")));
    }

    [Fact]
    public async Task Missing_read_backends_are_reported_instead_of_returning_empty_results()
    {
        var landingPads = new ShipcasterRpcService("shipCasterLandingPadMgr", Systems);
        var launchers = new ShipcasterRpcService("shipCasterLauncherMgr", Systems);

        await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await landingPads.CallAsync(Call("GetAllLandingPads")));
        await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await launchers.CallAsync(Call("GetFactionsWithShipcaster")));
    }

    [Fact]
    public async Task Explicitly_empty_state_returns_the_client_collection_shapes()
    {
        var landingPads = new ShipcasterRpcService("shipCasterLandingPadMgr", Systems, () => []);
        var launchers = new ShipcasterRpcService("shipCasterLauncherMgr", Systems, readFactionsWithShipcaster: () => []);

        Assert.Empty(Assert.IsType<PyList>(await landingPads.CallAsync(Call("GetAllLandingPads"))).Items);
        Assert.Empty(Assert.IsType<PyList>(await launchers.CallAsync(Call("GetFactionsWithShipcaster"))).Items);
    }

    private static ServiceCall Call(string method, params PyValue[] args) => Call(method, new ClientSession(), args);

    private static ServiceCall TravelCall(string method) => method == "CmdJumpThroughShipcaster"
        ? Call(method, new PyLong(77), new PyLong(30_000_001), new PyLong(90))
        : Call(method, new PyLong(77));

    private static ServiceCall Call(string method, ClientSession session, params PyValue[] args) => new()
    {
        Method = method,
        Args = args,
        Session = session,
    };

    private static PyObjectEx LandingPadWire(long itemId, int solarSystemId, int factionId, bool isBuilt, PyValue linkTimestamp, bool isDisrupted) => new(
        ObjectExKind.Ex1,
        new PyTuple([new PyToken("shipcaster.landingPadData.LandingPadData"), new PyList(
        [
            new PyLong(itemId),
            new PyInt(solarSystemId),
            new PyInt(factionId),
            isBuilt ? PyBool.True : PyBool.False,
            linkTimestamp,
            isDisrupted ? PyBool.True : PyBool.False,
        ])]),
        [],
        []);
}
