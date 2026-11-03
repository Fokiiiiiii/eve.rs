using Eve.Data;
using Eve.Dogma;
using Eve.Sde;
using Eve.Ships;
using Eve.Space;
using Npgsql;

namespace Eve.Services.Space.Tests;

public class TournamentPotentialMetricsTests
{
    [PgFact]
    public async Task Real_loaded_turret_uses_current_heat_and_distinguishes_idle_empty_and_incomplete()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        Assert.Equal(3396210, await sde.BuildAsync());
        var statics = await DogmaStatic.LoadAsync(sde);
        await statics.EnsureTypesAsync([587, 458, 246]);
        const long character = 70201, shipId = 70202, laser = 1, crystal = 3;
        var ship = new Eve.Inventory.Item(shipId, 587, character, 60003760, 0, 1, true, null);
        var weapon = new Eve.Inventory.Item(laser, 458, character, shipId, 27, 1, true, null);
        var charge = new Eve.Inventory.Item(crystal, 246, character, shipId, 27, 1000, true, null);
        ShipSnapshot Snapshot(ItemState state, bool loaded)
        {
            var world = new DogmaWorld(statics, character, shipId);
            world.Add(new DogmaItem(shipId, ship.TypeId, character, ship.LocationId, ship.Flag, ItemState.Online));
            world.Add(new DogmaItem(laser, weapon.TypeId, character, shipId, weapon.Flag, state));
            if (loaded) world.Add(new DogmaItem(crystal, charge.TypeId, character, shipId, charge.Flag, ItemState.Online));
            return new ShipSnapshot { Character = null!, Ship = ship, Fitted = [weapon], Charges = loaded ? [charge] : [], Skills = [], World = world };
        }
        var normal = Snapshot(ItemState.Active, true);
        var hot = Snapshot(ItemState.Overloaded, true);
        var enumerated = TournamentPotentialFit.Calculate(normal, statics, new Dictionary<long, string> { [laser] = "targetAttack" }, true);
        Assert.Null(enumerated.UnavailableReason);
        Assert.True(enumerated.Totals!.Value.FittedDamagePerSecond > 0);
        Assert.Equal(enumerated.Totals.Value.FittedDamagePerSecond, enumerated.Totals.Value.RunningDamagePerSecond);
        Assert.Equal("INCOMPLETE_FIT", TournamentPotentialFit.Calculate(normal, statics, new Dictionary<long, string>(), false).UnavailableReason);
        Assert.Equal("RUNNING_EFFECT_MISMATCH", TournamentPotentialFit.Calculate(normal, statics, new Dictionary<long, string> { [laser] = "differentEffect" }, true).UnavailableReason);
        var active = TournamentPotentialMetrics.Calculate(normal, statics, [new(laser, "targetAttack", true)], true);
        var idle = TournamentPotentialMetrics.Calculate(normal, statics, [new(laser, "targetAttack", false)], true);
        var overheated = TournamentPotentialMetrics.Calculate(hot, statics, [new(laser, "targetAttack", true)], true);
        Assert.Null(active.UnavailableReason);
        Assert.Null(idle.UnavailableReason);
        Assert.Null(overheated.UnavailableReason);
        Assert.True(active.Totals!.Value.FittedDamagePerSecond > 0);
        Assert.Equal(active.Totals.Value.FittedDamagePerSecond, active.Totals.Value.RunningDamagePerSecond);
        Assert.Equal(active.Totals.Value.FittedDamagePerSecond, idle.Totals!.Value.FittedDamagePerSecond);
        Assert.Equal(0, idle.Totals.Value.RunningDamagePerSecond);
        Assert.True(overheated.Totals!.Value.FittedDamagePerSecond > active.Totals.Value.FittedDamagePerSecond);
        Assert.Equal("TARGET_REQUIRED", ModulePackages.Of(normal, statics, laser, "targetAttack", 0, 1).Reason);
        var empty = TournamentPotentialMetrics.Calculate(Snapshot(ItemState.Active, false), statics, [new(laser, "targetAttack", false)], true);
        Assert.Null(empty.UnavailableReason);
        Assert.Equal(0, empty.Totals!.Value.FittedDamagePerSecond);
        Assert.Equal("INCOMPLETE_FIT", TournamentPotentialMetrics.Calculate(normal, statics, [], false).UnavailableReason);
        Assert.Equal("MODULE_NOT_FOUND", TournamentPotentialMetrics.Calculate(normal, statics, [new(999, "targetAttack", false)], true).UnavailableReason);
        Assert.Equal(0, TournamentPotentialMetrics.Calculate(normal, statics, [], true).Totals!.Value.FittedDamagePerSecond);
    }
}
