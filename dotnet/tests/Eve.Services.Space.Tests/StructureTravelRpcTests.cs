using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;

namespace Eve.Services.Space.Tests;

public sealed class StructureTravelRpcTests
{
    [Theory]
    [InlineData("structureCynoBeaconMgr", "CmdBridgeToStructureBeacon")]
    [InlineData("structureCynoBeaconMgr", "CmdGroupJumpToStructureBeacon")]
    [InlineData("structureCynoBeaconMgr", "CmdJumpToStructureBeacon")]
    [InlineData("structureJumpBridgeMgr", "CmdJumpThroughStructureStargate")]
    public async Task Travel_rpcs_are_advertised_and_forward_the_original_call(string serviceName, string method)
    {
        ServiceCall? forwarded = null;
        var expected = new PyInt(17);
        var service = new StructureTravelRpcService(serviceName, call =>
        {
            forwarded = call;
            return ValueTask.FromResult<PyValue>(expected);
        });
        var call = new ServiceCall
        {
            Method = method,
            Args = method == "CmdJumpThroughStructureStargate"
                ? [new PyLong(123)]
                : [new PyLong(123), new PyLong(30_000_142)],
            Session = new ClientSession(),
        };

        Assert.True(service.HasMethod(method));
        Assert.Same(expected, await service.CallAsync(call));
        Assert.Same(call, forwarded);
    }

    [Theory]
    [InlineData("structureCynoBeaconMgr", "CmdBridgeToStructureBeacon")]
    [InlineData("structureCynoBeaconMgr", "CmdGroupJumpToStructureBeacon")]
    [InlineData("structureCynoBeaconMgr", "CmdJumpToStructureBeacon")]
    [InlineData("structureJumpBridgeMgr", "CmdJumpThroughStructureStargate")]
    public async Task Travel_rpcs_report_the_missing_runtime_backend(string serviceName, string method)
    {
        var service = new StructureTravelRpcService(serviceName);
        var call = new ServiceCall
        {
            Method = method,
            Args = method == "CmdJumpThroughStructureStargate"
                ? [new PyLong(123)]
                : [new PyLong(123), new PyLong(30_000_142)],
            Session = new ClientSession(),
        };

        await Assert.ThrowsAsync<MachoWrappedException>(async () => await service.CallAsync(call));
    }

    [Fact]
    public async Task Unknown_method_keeps_the_service_none_fallback()
    {
        var service = new StructureTravelRpcService("structureCynoBeaconMgr", _ =>
            ValueTask.FromResult<PyValue>(new PyInt(1)));
        var call = new ServiceCall
        {
            Method = "UnknownTravelMethod",
            Args = [],
            Session = new ClientSession(),
        };

        Assert.False(service.HasMethod(call.Method));
        Assert.Same(PyNone.Instance, await service.CallAsync(call));
    }

    [Fact]
    public async Task Jump_bridge_lookup_rpcs_use_persisted_callbacks_and_reference_shapes()
    {
        long? destinationRequest = null;
        long? linkedRequest = null;
        var service = new StructureTravelRpcService(
            "structureJumpBridgeMgr",
            jumpBridgeDestination: (structureId, _) =>
            {
                destinationRequest = structureId;
                return ValueTask.FromResult<long?>(30_000_143);
            },
            linkedStructure: (structureId, _) =>
            {
                linkedRequest = structureId;
                return ValueTask.FromResult<(long StructureId, long SolarSystemId)?>((2002, 30_000_143));
            });
        var destinationCall = new ServiceCall
        {
            Method = "GetJbStructureDestination",
            Args = [new PyLong(2001)],
            Session = new ClientSession(),
        };
        var linkedCall = new ServiceCall
        {
            Method = "GetLinkedStructure",
            Args = [new PyLong(2001)],
            Session = new ClientSession(),
        };

        Assert.True(service.HasMethod(destinationCall.Method));
        Assert.True(service.HasMethod(linkedCall.Method));
        Assert.Equal(new PyInt(30_000_143), await service.CallAsync(destinationCall));
        var linked = Assert.IsType<PyList>(await service.CallAsync(linkedCall));
        Assert.Equal(new PyInt(2002), linked.Items[0]);
        Assert.Equal(new PyInt(30_000_143), linked.Items[1]);
        Assert.Equal(2001, destinationRequest);
        Assert.Equal(2001, linkedRequest);
    }

    [Theory]
    [InlineData("GetJbStructureDestination")]
    [InlineData("GetLinkedStructure")]
    public async Task Jump_bridge_lookups_return_none_when_not_found_or_unwired(string method)
    {
        var service = new StructureTravelRpcService("structureJumpBridgeMgr");
        var call = new ServiceCall
        {
            Method = method,
            Args = [new PyInt(2001)],
            Session = new ClientSession(),
        };

        Assert.Same(PyNone.Instance, await service.CallAsync(call));
    }
}
