using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;

namespace Eve.Services.Space.Tests;

public sealed class StructureBeaconTravelTests
{
    private const long CharacterId = 90_001;
    private const long StructureId = 30_000_142;
    private const int OriginSystemId = 30_000_001;
    private const int DestinationSystemId = 30_000_142;

    [Fact]
    public async Task Direct_jump_checks_service_acl_and_forwards_the_real_structure_target()
    {
        var serviceChecked = 0L;
        var jumpTarget = (StructureId: 0L, TypeId: 0, SolarSystemId: 0, BoundObjectId: (string?)null);
        var expected = new PyInt(1);
        var travel = new StructureBeaconTravel(
            (_, _) => ValueTask.FromResult<StructureBeaconInfo?>(new(StructureBeaconTravel.PharoluxCynoBeaconType, DestinationSystemId, true)),
            (_, _) => ValueTask.FromResult(false),
            (_, _, serviceId, _) =>
            {
                serviceChecked = serviceId;
                return ValueTask.FromResult(true);
            },
            (_, structureId, typeId, systemId, boundObjectId, _) =>
            {
                jumpTarget = (structureId, typeId, systemId, boundObjectId);
                return Task.FromResult<PyValue>(expected);
            });

        var call = Call("CmdJumpToStructureBeacon");
        var result = await travel.TravelAsync(call);

        Assert.Same(expected, result);
        Assert.Equal(16, serviceChecked);
        Assert.Equal((StructureId, StructureBeaconTravel.PharoluxCynoBeaconType, DestinationSystemId, "structureCynoBeaconMgr"), jumpTarget);
    }

    [Theory]
    [InlineData("CmdGroupJumpToStructureBeacon")]
    [InlineData("CmdBridgeToStructureBeacon")]
    public async Task Group_and_bridge_calls_do_not_fall_through_to_a_single_pilot_jump(string method)
    {
        var jumpCalled = false;
        var travel = new StructureBeaconTravel(
            (_, _) => ValueTask.FromResult<StructureBeaconInfo?>(new(StructureBeaconTravel.PharoluxCynoBeaconType, DestinationSystemId, true)),
            (_, _) => ValueTask.FromResult(false),
            (_, _, _, _) => ValueTask.FromResult(true),
            (_, _, _, _, _, _) =>
            {
                jumpCalled = true;
                return Task.FromResult<PyValue>(new PyInt(1));
            });

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => travel.TravelAsync(Call(method)).AsTask());

        Assert.Contains("not supported", NotifyText(error));
        Assert.False(jumpCalled);
    }

    [Fact]
    public async Task Missing_structure_service_access_refuses_before_the_jump()
    {
        var jumpCalled = false;
        var travel = new StructureBeaconTravel(
            (_, _) => ValueTask.FromResult<StructureBeaconInfo?>(new(StructureBeaconTravel.PharoluxCynoBeaconType, DestinationSystemId, true)),
            (_, _) => ValueTask.FromResult(false),
            (_, _, _, _) => ValueTask.FromResult(false),
            (_, _, _, _, _, _) =>
            {
                jumpCalled = true;
                return Task.FromResult<PyValue>(new PyInt(1));
            });

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => travel.TravelAsync(Call("CmdJumpToStructureBeacon")).AsTask());

        Assert.Contains("do not have access", NotifyText(error));
        Assert.False(jumpCalled);
    }

    private static ServiceCall Call(string method)
    {
        var session = new ClientSession();
        session.Set("charid", new PyLong(CharacterId));
        session.Set("solarsystemid2", new PyInt(OriginSystemId));
        return new ServiceCall
        {
            Method = method,
            Args = [new PyLong(StructureId), new PyLong(DestinationSystemId)],
            Session = session,
            BoundObjectId = "structureCynoBeaconMgr",
            Cancellation = CancellationToken.None,
        };
    }

    private static string? NotifyText(MachoWrappedException error) =>
        error.Error.Payload is PyObjectEx { Header: PyTuple { Items: [_, PyTuple { Items: [_, PyDict arguments] }, ..] } }
            && arguments.Entries.FirstOrDefault(entry => entry.Key is PyStr { Value: "notify" }).Value is PyStr text
                ? text.Value
                : null;
}
