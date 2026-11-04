using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// A hardener that starts and stops in space changes the resonances of the ship, so the simulation is sent the ship again on each change (the one shared
/// path, <c>SpaceSessions</c>): started, stopped, and taken offline. What the simulation is sent is what dogma then has.
/// </summary>
public class PolarizedHostTests
{
    private const int EmShieldHardener = 2293, MidSlot = 19, EmShield = 271;
    private const string EmShieldEffect = "modifyActiveShieldResonancePostPercent";

    private static double ShieldEmOf(SimShipChanged changed) => changed.Ship.Vitals!.Resonances.Shield[0];

    [PgFact]
    public async Task Starting_stopping_and_taking_offline_a_hardener_sends_the_ship_to_the_simulation_each_time()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, hardener) = await RepairHostTests.InSpaceWithAsync(rig, EmShieldHardener, MidSlot);
        var bare = (await rig.Ships.LoadAsync(character.CharacterId, CancellationToken.None))!.World.Get(character.ShipId, EmShield);

        Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(session, hardener, EmShieldEffect, 0, 1000, CancellationToken.None));
        Assert.Equal(bare * 0.5, ShieldEmOf(Assert.IsType<SimShipChanged>(rig.Sim.Heard.Last())), 9);

        Assert.Equal(ModuleResult.Done, await rig.Space.DeactivateAsync(session, hardener, EmShieldEffect, CancellationToken.None));
        Assert.Equal(bare, ShieldEmOf(Assert.IsType<SimShipChanged>(rig.Sim.Heard.Last())), 9);

        Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(session, hardener, EmShieldEffect, 0, 1000, CancellationToken.None));
        Assert.Equal(bare * 0.5, ShieldEmOf(Assert.IsType<SimShipChanged>(rig.Sim.Heard.Last())), 9);

        // taken offline, the hardener no longer runs: the ship is sent again without it (the DogmaIM switch asks for the refresh)
        Assert.Equal(ModuleResult.Done, await rig.Space.OfflineAsync(session, hardener, CancellationToken.None));
        await rig.Space.RefreshShipAsync(session, CancellationToken.None);
        Assert.Equal(bare, ShieldEmOf(Assert.IsType<SimShipChanged>(rig.Sim.Heard.Last())), 9);
    }
}
