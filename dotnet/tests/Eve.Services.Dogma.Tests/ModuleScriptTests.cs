namespace Eve.Services.Dogma.Tests;

/// <summary>
/// A script is a charge loaded in a module's slot: its effects reach the module it sits beside (the other item of the same slot, <c>otherID</c>). The
/// SDE rows used here: Tracking Computer II (1978) carries trackingSpeedBonus 767 = 15 and maxRangeBonus 351 = 7.5, and effect 4559 applies both to the
/// Gunnery turrets fitted to the ship. Tracking Speed Script (29001) carries +100 on the module's trackingSpeedBonusBonus and -100 on its maxRangeBonusBonus;
/// Optimal Range Script (28999) carries the mirror image.
/// </summary>
public class ModuleScriptTests
{
    private const int TrackingComputer = 1978;
    private const int Turret = 484; // 125mm Gatling AutoCannon I, group 55
    private const int TrackingSpeedScript = 29001;
    private const int OptimalRangeScript = 28999;
    private const int TrackingSpeed = 160;
    private const int MaxRange = 54;
    private const int TrackingSpeedBonus = 767;
    private const int MaxRangeBonus = 351;

    private static async Task<(Rig Rig, Eve.Dogma.DogmaWorld World, long ShipId, long CharacterId, Func<int, int, Eve.Dogma.ItemState, Eve.Dogma.DogmaItem> Add)> ShipAsync()
    {
        var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var snapshot = await rig.Ships.LoadAsync(character.CharacterId);
        Assert.NotNull(snapshot);
        await rig.Statics.EnsureTypesAsync([TrackingComputer, Turret, TrackingSpeedScript, OptimalRangeScript]);
        var shipId = snapshot.Ship.Id;
        var world = snapshot.World;
        var nextItemId = long.MaxValue - 2_000;
        Eve.Dogma.DogmaItem Add(int typeId, int flag, Eve.Dogma.ItemState state) =>
            world.Add(new Eve.Dogma.DogmaItem(nextItemId++, typeId, character.CharacterId, shipId, flag, state));
        return (rig, world, shipId, character.CharacterId, Add);
    }

    [PgFact]
    public async Task A_tracking_speed_script_loaded_in_a_tracking_computer_doubles_its_bonus_and_takes_the_range_bonus_away()
    {
        var (rig, world, _, _, add) = await ShipAsync();
        await using var _rig = rig;
        var turret = add(Turret, 27, Eve.Dogma.ItemState.Online);
        var baseTracking = world.Get(turret.Id, TrackingSpeed);
        var baseRange = world.Get(turret.Id, MaxRange);

        var computer = add(TrackingComputer, 11, Eve.Dogma.ItemState.Active); // an activated module: effect 4559 is an activation effect
        Assert.Equal(15, world.Get(computer.Id, TrackingSpeedBonus), 9);
        Assert.Equal(baseTracking * 1.15, world.Get(turret.Id, TrackingSpeed), 6);
        Assert.Equal(baseRange * 1.075, world.Get(turret.Id, MaxRange), 6);

        // the script sits beside the computer in its slot: it doubles the computer's tracking bonus and removes its range bonus
        add(TrackingSpeedScript, 11, Eve.Dogma.ItemState.Online);
        Assert.Equal(30, world.Get(computer.Id, TrackingSpeedBonus), 9);
        Assert.Equal(0, world.Get(computer.Id, MaxRangeBonus), 9);
        Assert.Equal(baseTracking * 1.30, world.Get(turret.Id, TrackingSpeed), 6);
        Assert.Equal(baseRange, world.Get(turret.Id, MaxRange), 6);
    }

    [PgFact]
    public async Task An_optimal_range_script_loaded_in_a_tracking_computer_removes_its_tracking_bonus_and_doubles_its_range_bonus()
    {
        var (rig, world, _, _, add) = await ShipAsync();
        await using var _rig = rig;
        var turret = add(Turret, 27, Eve.Dogma.ItemState.Online);
        var baseTracking = world.Get(turret.Id, TrackingSpeed);
        var baseRange = world.Get(turret.Id, MaxRange);

        var computer = add(TrackingComputer, 11, Eve.Dogma.ItemState.Active); // an activated module: effect 4559 is an activation effect
        add(OptimalRangeScript, 11, Eve.Dogma.ItemState.Online);
        Assert.Equal(0, world.Get(computer.Id, TrackingSpeedBonus), 9);
        Assert.Equal(15, world.Get(computer.Id, MaxRangeBonus), 9);
        Assert.Equal(baseTracking, world.Get(turret.Id, TrackingSpeed), 6);
        Assert.Equal(baseRange * 1.15, world.Get(turret.Id, MaxRange), 6);
    }
}
