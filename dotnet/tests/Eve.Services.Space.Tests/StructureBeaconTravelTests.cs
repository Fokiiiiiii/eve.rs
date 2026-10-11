using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Xunit;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The checks of a structure cyno beacon jump (EveJS <c>resolveUsableBeacon</c>), each refusal a named notice with the reference's text. A beacon that passes
/// every check is refused by name too: the jump itself is not placed in the simulation yet.
/// </summary>
public class StructureBeaconTravelTests
{
    private const long Beacon = 1_000_000_000_010, Here = 30_000_001, Target = 30_000_142;

    private sealed class Sink : IPacketSink
    {
        public void Send(PyValue packet) { }
    }

    private static StructureBeaconTravel Travel(StructureBeaconInfo? beacon, bool jammed = false) =>
        new((_, _) => ValueTask.FromResult(beacon), (_, _) => ValueTask.FromResult(jammed));

    private static ServiceCall Call(long structureId, long requestedSystem, long? here)
    {
        var session = new ClientSession(new Sink());
        session.Set("charid", new PyInt(20));
        if (here is { } system)
            session.Set("solarsystemid2", new PyLong(system));
        return new ServiceCall { Method = "CmdJumpToStructureBeacon", Args = [new PyLong(structureId), new PyLong(requestedSystem)], Session = session };
    }

    private static StructureBeaconInfo Pharolux(bool online = true, long system = Target) => new(StructureBeaconTravel.PharoluxCynoBeaconType, system, online);

    private static string Notice(Exception exception)
    {
        var error = Assert.IsType<MachoWrappedException>(exception).Error;
        Assert.Equal("CustomNotify", error.UserErrorMessage);
        var arguments = ((PyTuple)((PyTuple)((PyObjectEx)error.Payload).Header).Items[1]).Items;
        return ((PyStr)((PyDict)arguments[1]).Entries.Single(e => JsSemantics.StringValue(e.Key) == "notify").Value).Value;
    }

    [Fact]
    public async Task A_beacon_that_is_gone_or_is_not_a_pharolux_is_no_longer_available()
    {
        Assert.Equal("That cynosural beacon is no longer available.",
            Notice(await Assert.ThrowsAsync<MachoWrappedException>(() => Travel(null).TravelAsync(Call(Beacon, Target, Here)).AsTask())));
        Assert.Equal("That cynosural beacon is no longer available.",
            Notice(await Assert.ThrowsAsync<MachoWrappedException>(() => Travel(new StructureBeaconInfo(35832, Target, true)).TravelAsync(Call(Beacon, Target, Here)).AsTask())));
    }

    [Fact]
    public async Task A_beacon_in_another_system_than_the_one_asked_for_is_refused()
    {
        Assert.Equal("That cynosural beacon is not in the requested solar system.",
            Notice(await Assert.ThrowsAsync<MachoWrappedException>(() => Travel(Pharolux()).TravelAsync(Call(Beacon, Here + 5, Here)).AsTask())));
    }

    [Fact]
    public async Task A_pilot_outside_space_or_already_in_the_system_is_refused()
    {
        Assert.Equal("You must be in space to use a cynosural beacon.",
            Notice(await Assert.ThrowsAsync<MachoWrappedException>(() => Travel(Pharolux()).TravelAsync(Call(Beacon, Target, null)).AsTask())));
        Assert.Equal("You are already in that solar system.",
            Notice(await Assert.ThrowsAsync<MachoWrappedException>(() => Travel(Pharolux()).TravelAsync(Call(Beacon, Target, Target)).AsTask())));
    }

    [Fact]
    public async Task A_beacon_whose_service_is_offline_or_whose_system_is_jammed_is_refused()
    {
        Assert.Equal("That cynosural beacon service is offline.",
            Notice(await Assert.ThrowsAsync<MachoWrappedException>(() => Travel(Pharolux(online: false)).TravelAsync(Call(Beacon, Target, Here)).AsTask())));
        Assert.Equal("That solar system is cynosural jammed.",
            Notice(await Assert.ThrowsAsync<MachoWrappedException>(() => Travel(Pharolux(), jammed: true).TravelAsync(Call(Beacon, Target, Here)).AsTask())));
    }

    [Fact]
    public async Task A_beacon_that_passes_every_check_is_refused_by_name_not_answered_empty()
    {
        Assert.Equal("Jumping to a structure cynosural beacon is not wired in the simulation yet: the beacon's field is not placed.",
            Notice(await Assert.ThrowsAsync<MachoWrappedException>(() => Travel(Pharolux()).TravelAsync(Call(Beacon, Target, Here)).AsTask())));
    }
}
