using Eve.Dogma;
using Eve.Inventory;

namespace Eve.Ships.Tests;

/// <summary>
/// The mining profile of a drone and of a module, read from the real static data by attribute name, and one mining cycle that both sources share.
/// </summary>
public class MiningProfileTests
{
    private const int MiningDroneI = 10246, MiningDroneII = 10250, MinerI = 483, ModulatedStripMinerII = 17912, Veldspar = 1230;
    private const long Pilot = 1, Ship = 2, DroneItem = 9_001, ModuleItem = 9_002;

    /// <summary>A world with items of the real static data, in the places they are given (a drone out in space, a module in a slot).</summary>
    private static async Task<DogmaWorld> WorldWithAsync(Rig rig, params (long Id, int TypeId, int Flag)[] items)
    {
        await rig.Statics.EnsureTypesAsync(items.Select(item => item.TypeId));
        var world = new DogmaWorld(rig.Statics, Pilot, Ship);
        foreach (var (id, typeId, flag) in items)
        {
            world.Add(new DogmaItem(id, typeId, Pilot, Ship, flag));
        }
        return world;
    }

    [PgFact]
    public async Task A_mining_drone_I_reads_its_yield_cycle_and_waste_from_the_static_data()
    {
        await using var rig = await Rig.CreateAsync();
        var world = await WorldWithAsync(rig, (DroneItem, MiningDroneI, CharacterShips.InSpaceFlag));

        Assert.Equal(new MiningProfile(25, 60_000, 0, 0, 0), MiningProfile.From(world, rig.Statics, DroneItem));
    }

    [PgFact]
    public async Task A_mining_drone_II_reads_its_waste_probability_and_volume_multiplier()
    {
        await using var rig = await Rig.CreateAsync();
        var world = await WorldWithAsync(rig, (DroneItem, MiningDroneII, CharacterShips.InSpaceFlag));

        Assert.Equal(new MiningProfile(33, 60_000, 1, 34, 0), MiningProfile.From(world, rig.Statics, DroneItem));
    }

    [PgFact]
    public async Task A_fitted_miner_I_reads_the_same_attributes_as_a_drone_of_its_kind()
    {
        await using var rig = await Rig.CreateAsync();
        var world = await WorldWithAsync(rig, (ModuleItem, MinerI, ItemFlags.HiSlot0));

        // a laser also carries its crit chance (1 %) and crit yield (twice the normal), which a drone does not
        Assert.Equal(new MiningProfile(10, 15_000, 0, 0, 0, 0.01, 2), MiningProfile.From(world, rig.Statics, ModuleItem));
    }

    [PgFact]
    public async Task A_fitted_modulated_strip_miner_II_reads_its_waste_from_the_static_data()
    {
        await using var rig = await Rig.CreateAsync();
        var world = await WorldWithAsync(rig, (ModuleItem, ModulatedStripMinerII, ItemFlags.HiSlot0));

        Assert.Equal(new MiningProfile(120, 45_000, 1, 34, 0, 0.01, 2), MiningProfile.From(world, rig.Statics, ModuleItem));
    }

    [PgFact]
    public async Task A_drone_and_a_module_of_the_same_type_give_the_same_profile()
    {
        await using var rig = await Rig.CreateAsync();
        var world = await WorldWithAsync(
            rig,
            (DroneItem, MiningDroneII, CharacterShips.InSpaceFlag),
            (ModuleItem, MiningDroneII, ItemFlags.HiSlot0));

        var drone = MiningProfile.From(world, rig.Statics, DroneItem);
        var module = MiningProfile.From(world, rig.Statics, ModuleItem);
        Assert.Equal(drone, module);
        Assert.Equal(new MiningProfile(33, 60_000, 1, 34, 0), drone);
    }

    [PgFact]
    public async Task A_drone_and_a_fitted_module_mine_through_one_cycle_and_each_source_refuses_the_other()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.Statics.EnsureTypesAsync([MiningDroneI, MinerI, Veldspar]);
        var c = await rig.NewCharacterAsync();
        await rig.Ships.UndockAsync(c.CharacterId);
        var system = (int)(await rig.Items.GetAsync(c.ShipId))!.LocationId;
        var drone = await rig.GiveAsync(c.CharacterId, MiningDroneI, singleton: true, quantity: 1, location: system, flag: CharacterShips.InSpaceFlag);
        var module = await rig.GiveAsync(c.CharacterId, MinerI, singleton: true, quantity: 1, location: c.ShipId, flag: ItemFlags.HiSlot0);
        var rock = await rig.GiveAsync(c.CharacterId, Veldspar, singleton: false, quantity: 1000, location: system, flag: CharacterShips.InSpaceFlag);
        var schema = $"sp_{Guid.NewGuid():N}"[..14];
        await PgSpaceItemStore.MigrateAsync(rig.Source, schema);
        var spaceItems = new PgSpaceItemStore(rig.Source, schema);
        var ledger = new PgWorkCycleLedger(rig.Source, schema);
        // the numbers of a cycle of 10 m3 with no waste and no crit; the rolls are 0.5 (a drone uses only the waste roll)
        var numbers = new MiningNumbers(10, 0, 0, 0, 0);
        var rolls = new MiningRolls(0.5, 0.5, 0.5, 0.5, 0.5);

        // a drone is refused as a module, and a module as a drone: nothing is mined
        var asModule = await rig.Ships.MineAsteroidAsync(c.CharacterId, c.ShipId, system, drone, rock, MiningSource.Module, numbers, rolls, 1, ledger, spaceItems);
        var asDrone = await rig.Ships.MineAsteroidAsync(c.CharacterId, c.ShipId, system, module, rock, MiningSource.Drone, numbers, rolls, 1, ledger, spaceItems);
        Assert.Equal(("NOT_CONTROLLED", false), (asModule.Reason, asModule.Accepted));
        Assert.Equal(("NOT_CONTROLLED", false), (asDrone.Reason, asDrone.Accepted));

        // 10 m3 of Veldspar (0.1 m3 a unit) is 100 units, for either source: the rock keeps what is left
        var byModule = await rig.Ships.MineAsteroidAsync(c.CharacterId, c.ShipId, system, module, rock, MiningSource.Module, numbers, rolls, 2, ledger, spaceItems);
        Assert.Equal((true, "", 900L, 100L, false), (byModule.Accepted, byModule.Reason, byModule.UnitsRemaining, byModule.DeliveredUnits, byModule.Depleted));

        // the same cycle again is answered from its record inside the grant transaction: the rock gives nothing more
        var again = await rig.Ships.MineAsteroidAsync(c.CharacterId, c.ShipId, system, module, rock, MiningSource.Module, numbers, rolls, 2, ledger, spaceItems);
        Assert.Equal((true, "", 900L, 100L, false), (again.Accepted, again.Reason, again.UnitsRemaining, again.DeliveredUnits, again.Depleted));
        Assert.Equal(900L, (await rig.Items.GetAsync(rock))!.Quantity);

        var type = rig.Statics.Type(MiningDroneI);
        Assert.Equal((true, true, 18, "Drone"), ((await rig.Items.GetAsync(drone)) is not null, type is not null, type?.CategoryId ?? -1, rig.Statics.CategoryName(type?.CategoryId ?? -1)));

        var byDrone = await rig.Ships.MineAsteroidAsync(c.CharacterId, c.ShipId, system, drone, rock, MiningSource.Drone, numbers, rolls, 3, ledger, spaceItems);
        Assert.Equal((true, "", 800L, 100L, false), (byDrone.Accepted, byDrone.Reason, byDrone.UnitsRemaining, byDrone.DeliveredUnits, byDrone.Depleted));
    }
}
