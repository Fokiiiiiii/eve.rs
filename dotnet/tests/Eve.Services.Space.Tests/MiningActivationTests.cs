namespace Eve.Services.Space.Tests;

/// <summary>A mining laser is activated on a rock of its own family only (M3): the host refuses the other family before the simulation hears of it.</summary>
public class MiningActivationTests
{
    [PgFact]
    public async Task An_ore_laser_is_refused_on_an_ice_rock()
    {
        await using var rig = await Rig.CreateAsync();
        var types = await new Eve.Sde.SdeReader(rig.Source).ReadTypeSummariesAsync();
        var oreLaser = DroneHostTests.SdeTypeId(types, "Miner I");
        var iceRock = DroneHostTests.SdeTypeId(types, "Clear Icicle");
        await rig.Statics.EnsureTypesAsync([oreLaser, iceRock]);
        var pilot = await DroneHostTests.FlyingWithADroneAsync(rig, minerTypeId: oreLaser);
        var rock = (await rig.Ships.CreateAsteroidAsync(iceRock, pilot.System, 0, 1, [0d, 0d, 0d], [0d, 0d, 1d], 10d, rig.SpaceItems)).Item.Id;

        var refused = await rig.Space.ActivateAsync(pilot.Session, pilot.Module, "miningLaser", rock, 1, CancellationToken.None);

        Assert.Equal("TARGET_INVALID_FOR_MODULE", refused?.Reason);
    }

    [PgFact]
    public async Task An_ice_laser_is_refused_on_an_ore_rock()
    {
        await using var rig = await Rig.CreateAsync();
        var types = await new Eve.Sde.SdeReader(rig.Source).ReadTypeSummariesAsync();
        var iceLaser = DroneHostTests.SdeTypeId(types, "Ice Mining Laser I");
        var oreRock = DroneHostTests.SdeTypeId(types, "Veldspar");
        await rig.Statics.EnsureTypesAsync([iceLaser, oreRock]);
        var pilot = await DroneHostTests.FlyingWithADroneAsync(rig, minerTypeId: iceLaser);
        var rock = (await rig.Ships.CreateAsteroidAsync(oreRock, pilot.System, 0, 1, [0d, 0d, 0d], [0d, 0d, 1d], 10d, rig.SpaceItems)).Item.Id;

        var refused = await rig.Space.ActivateAsync(pilot.Session, pilot.Module, "miningLaser", rock, 1, CancellationToken.None);

        Assert.Equal("TARGET_INVALID_FOR_MODULE", refused?.Reason);
    }
}
