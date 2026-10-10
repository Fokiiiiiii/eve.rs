using Eve.Dogma;
using Eve.Inventory;

namespace Eve.Ships.Tests;

/// <summary>
/// A running siege module (SDE 20280, effect 6582 <c>moduleBonusSiegeModule</c>) locks its Dreadnought: maxVelocity to zero (SDE modifier on 37), the
/// warp scramble to 100 (104, from 852), and docking off (2354), and only while it runs (SF-10; module_rules WP list). The one-per-ship rule is the
/// simulation's group limit (maxGroupActive 763 = 1, <c>modules/activation.rs</c>).
/// </summary>
public class SiegeModuleTests
{
    private const int Moros = 19724, SiegeModuleI = 20280, HighSlot = 27;
    private const int MaxVelocity = 37, WarpScrambleStatus = 104, DisallowDocking = 2354;

    /// <summary>A module the pilot started is Active in the ship's world (what space does with a started module); the store keeps Online and Offline only.</summary>
    private static async Task<ShipSnapshot> WithStateAsync(Rig rig, long characterId, long moduleId, ItemState state)
    {
        var snapshot = await rig.Ships.LoadAsync(characterId);
        Assert.NotNull(snapshot);
        snapshot.World.SetState(moduleId, state);
        return snapshot;
    }

    [PgFact]
    public async Task A_running_siege_module_locks_its_dreadnought_and_only_while_it_runs()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync(shipTypeId: Moros);
        await rig.Statics.EnsureTypesAsync([SiegeModuleI]);
        foreach (var skill in rig.Statics.Type(SiegeModuleI)!.RequiredSkills)
        {
            await rig.SetSkillAsync(c.CharacterId, skill, 5);
        }
        var siege = await rig.GiveAsync(c.CharacterId, SiegeModuleI);
        Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(c.CharacterId, siege, Rig.Station, c.ShipId, HighSlot, 1));

        var idle = await rig.Ships.LoadAsync(c.CharacterId);
        Assert.NotNull(idle);
        Assert.True(idle.World.Get(c.ShipId, MaxVelocity) > 0);
        Assert.Equal(0, idle.World.Get(c.ShipId, WarpScrambleStatus), 6);

        var running = await WithStateAsync(rig, c.CharacterId, siege, ItemState.Active);
        Assert.Equal(0, running.World.Get(c.ShipId, MaxVelocity), 6);
        Assert.Equal(100, running.World.Get(c.ShipId, WarpScrambleStatus), 6);
        Assert.Equal(1, running.World.Get(c.ShipId, DisallowDocking), 6);

        var stopped = await WithStateAsync(rig, c.CharacterId, siege, ItemState.Online);
        Assert.True(stopped.World.Get(c.ShipId, MaxVelocity) > 0);
        Assert.Equal(0, stopped.World.Get(c.ShipId, WarpScrambleStatus), 6);
    }
}
