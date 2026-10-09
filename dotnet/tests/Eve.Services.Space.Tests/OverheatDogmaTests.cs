using Eve.Data;
using Eve.Dogma;
using Eve.Sde;
using Eve.Ships;
using Eve.Space;
using Npgsql;

namespace Eve.Services.Space.Tests;

/// <summary>
/// An overheated module that runs is Overloaded to dogma (FP-C4a): its overload effect (3025, overloadSelfDamageBonus) applies to the next shot the
/// host builds for it, and the same turret that is not overheated builds the bare numbers. The turret is the real SDE's Heavy Pulse Laser I on a Rifter.
/// </summary>
public class OverheatDogmaTests
{
    private const long CharacterId = 70201, ShipId = 70202, Station = 60003760, Laser = 1, Crystal = 3, Target = 2;
    private const int Rifter = 587, HeavyPulseLaser = 458, MultifrequencyS = 246, HighSlot = 27;

    /// <summary>A Rifter with the turret fitted in the given state, as the snapshot of a pilot in space.</summary>
    private static async Task<(ShipSnapshot Snapshot, DogmaStatic Statics)> ShipAsync(ItemState turret)
    {
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var statics = await DogmaStatic.LoadAsync(new SdeReader(source));
        await statics.EnsureTypesAsync([Rifter, HeavyPulseLaser, MultifrequencyS]);
        var world = new DogmaWorld(statics, CharacterId, ShipId);
        world.Add(new DogmaItem(ShipId, Rifter, CharacterId, Station, 0, ItemState.Online));
        world.Add(new DogmaItem(Laser, HeavyPulseLaser, CharacterId, ShipId, HighSlot, turret));
        // a pulse laser fires a crystal loaded in its slot (a charge is an item of the ship with the module's flag)
        world.Add(new DogmaItem(Crystal, MultifrequencyS, CharacterId, ShipId, HighSlot, ItemState.Online));
        var snapshot = new ShipSnapshot
        {
            Character = null!,
            Ship = new Eve.Inventory.Item(ShipId, Rifter, CharacterId, Station, 0, 1, true, null),
            Fitted = [new Eve.Inventory.Item(Laser, HeavyPulseLaser, CharacterId, ShipId, HighSlot, 1, true, null)],
            Charges = [new Eve.Inventory.Item(Crystal, MultifrequencyS, CharacterId, ShipId, HighSlot, 1000, true, null)],
            Skills = [],
            World = world,
        };
        return (snapshot, statics);
    }

    [PgFact]
    public async Task An_overheated_turret_builds_its_shot_with_the_overload_bonus_and_a_bare_one_builds_the_base()
    {
        var (bare, statics) = await ShipAsync(ItemState.Active);
        var (hot, _) = await ShipAsync(ItemState.Overloaded);

        var normal = ModulePackages.Of(bare, statics, Laser, "targetAttack", Target, 1);
        var overheated = ModulePackages.Of(hot, statics, Laser, "targetAttack", Target, 1);

        Assert.Null(normal.Reason);
        Assert.Null(overheated.Reason);
        var baseDamage = Assert.IsType<Eve.Space.SimEffect>(normal.Effect).Turret!.RawDamage;
        var boosted = Assert.IsType<Eve.Space.SimEffect>(overheated.Effect).Turret!.RawDamage;
        Assert.True(boosted.Sum() > baseDamage.Sum(), $"overheated {boosted.Sum()} is not above the base {baseDamage.Sum()}");
    }
}
