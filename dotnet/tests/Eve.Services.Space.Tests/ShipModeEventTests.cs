namespace Eve.Services.Space.Tests;

/// <summary>
/// A module of a pilot's ship that starts or stops is announced on the bus (<see cref="ShipModeTopics"/>); a siege module is also announced as the siege
/// topics. Nothing subscribes to them yet: the test reads what was announced (<see cref="Rig.Announced"/>).
/// </summary>
public class ShipModeEventTests
{
    private const int SiegeModuleI = 20280;
    private const string SiegeEffect = "moduleBonusSiegeModule";

    [PgFact]
    public async Task A_siege_module_that_starts_and_stops_is_announced_as_a_module_and_as_the_siege_mode()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.UseBusAsync();
        var flying = await DroneHostTests.FlyingWithADroneAsync(rig, minerTypeId: SiegeModuleI);

        var started = await rig.Space.ActivateAsync(flying.Session, flying.Module, SiegeEffect, 0, 1, CancellationToken.None);
        Assert.True(started!.Ok, started.Reason);
        var stopped = await rig.Space.DeactivateAsync(flying.Session, flying.Module, SiegeEffect, CancellationToken.None);
        Assert.True(stopped!.Ok, stopped.Reason);

        // the subscriber is delivered by pumping the bus; the announcements are made off the calling thread, so the bus is pumped until they arrive
        for (var attempt = 0; attempt < 100 && Topics(rig).Count < 4; attempt++)
        {
            await rig.Bus!.PumpAsync();
            await Task.Delay(100);
        }
        // the announcements are made off the calling thread, so what is compared is the set of them
        string[] expected = [ShipModeTopics.ModuleStarted, ShipModeTopics.SiegeEntered, ShipModeTopics.ModuleStopped, ShipModeTopics.SiegeLeft];
        Assert.Equal(expected.Order(), Topics(rig).Order());
    }

    private static List<string> Topics(Rig rig)
    {
        lock (rig.Announced)
        {
            return rig.Announced.Select(a => a.Topic).Where(t => t.StartsWith("ship.", StringComparison.Ordinal)).ToList();
        }
    }
}
