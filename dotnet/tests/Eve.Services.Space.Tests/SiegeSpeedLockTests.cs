using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// A started siege module locks its ship's speed (SDE 6582: maxVelocity 37 times zero). The simulation has the speed only from the ship it is sent, so a start
/// must send the ship again with its new maximum velocity, as it does for the resonances (SF-10).
/// </summary>
public class SiegeSpeedLockTests
{
    private const int SiegeModuleI = 20280;
    private const string SiegeEffect = "moduleBonusSiegeModule";

    [PgFact]
    public async Task A_started_siege_module_sends_the_ship_to_the_simulation_with_a_zero_maximum_velocity()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await DroneHostTests.FlyingWithADroneAsync(rig, minerTypeId: SiegeModuleI);
        var heard = rig.Sim.Heard.Count;

        var started = await rig.Space.ActivateAsync(flying.Session, flying.Module, SiegeEffect, 0, 1, CancellationToken.None);

        Assert.True(started!.Ok, started.Reason);
        var sent = Assert.Single(rig.Sim.Heard.Skip(heard).OfType<SimShipChanged>());
        Assert.Equal(0, sent.Ship.MaxVelocity, 6);
    }
}
