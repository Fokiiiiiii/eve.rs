using Eve.Characters;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Inventory;

namespace Eve.Ships.Tests;

/// <summary>
/// AssembleShip of a Strategic Cruiser (SF-4): one subsystem of each class completes a packaged hull in the station's hangar, a choice that does not complete it
/// assembles nothing, and a fitted hull swaps a subsystem in the station by the fitting rules (EveJS <c>Handle_AssembleShip</c>,
/// <c>_buildStrategicCruiserAssemblyPlan</c>, <c>liveFittingState.js</c>; the client's <c>invItemFunctions.AssembleShip</c>).
/// </summary>
public class StrategicCruiserAssemblyTests
{
    private const int Tengu = 29984, Legion = 29986, Cockroach = 11019;
    private const int TenguCore = 45625, TenguDefensive = 45589, TenguDefensiveTwo = 45590, TenguOffensive = 45601, TenguPropulsion = 45613;
    private const int LegionDefensive = 45586;
    private const int Core = 125, Defensive = 126;

    private static async Task<long[]> TenguSubsystemsAsync(Rig rig, long owner, params int[] types)
    {
        var ids = new List<long>();
        foreach (var type in types)
        {
            ids.Add(await rig.GiveAsync(owner, type));
        }
        return [.. ids];
    }

    [PgFact]
    public async Task One_subsystem_of_each_class_assembles_a_packaged_tengu_and_fits_it_offline_with_its_name()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var hull = await rig.GiveAsync(c.CharacterId, Tengu, singleton: false);
        var subsystems = await TenguSubsystemsAsync(rig, c.CharacterId, TenguCore, TenguDefensive, TenguOffensive, TenguPropulsion);

        var changed = await rig.Ships.AssembleAsync(c.CharacterId, Rig.Station, [hull], subsystems, "  Night\r\nowl  ");

        var assembled = Assert.Single(changed);
        Assert.Equal(hull, assembled.ItemId);
        Assert.Equal(4, assembled.Items.Count);
        var stored = await rig.Items.GetAsync(hull);
        Assert.Equal((true, "Night owl"), (stored!.Singleton, stored.CustomName));
        var fitted = await rig.InShipAsync(c.CharacterId, hull);
        Assert.Equal(
            new[] { (TenguCore, 125, ItemState.Offline), (TenguDefensive, 126, ItemState.Offline), (TenguOffensive, 127, ItemState.Offline), (TenguPropulsion, 128, ItemState.Offline) },
            fitted);
    }

    [PgFact]
    public async Task A_choice_that_leaves_a_class_out_assembles_nothing_and_takes_nothing_out_of_the_hangar()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var hull = await rig.GiveAsync(c.CharacterId, Tengu, singleton: false);
        var subsystems = await TenguSubsystemsAsync(rig, c.CharacterId, TenguCore, TenguDefensive, TenguOffensive);

        Assert.Empty(await rig.Ships.AssembleAsync(c.CharacterId, Rig.Station, [hull], subsystems, null));

        Assert.False((await rig.Items.GetAsync(hull))!.Singleton);
        foreach (var id in subsystems)
        {
            Assert.Equal(Rig.Station, (await rig.Items.GetAsync(id))!.LocationId);
        }
    }

    [PgFact]
    public async Task A_subsystem_of_another_hull_or_a_second_subsystem_of_one_class_is_refused()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var hull = await rig.GiveAsync(c.CharacterId, Tengu, singleton: false);
        var legion = await rig.GiveAsync(c.CharacterId, LegionDefensive);
        var other = await TenguSubsystemsAsync(rig, c.CharacterId, TenguCore, TenguOffensive, TenguPropulsion);

        Assert.Empty(await rig.Ships.AssembleAsync(c.CharacterId, Rig.Station, [hull], [.. other, legion], null));
        var twice = await TenguSubsystemsAsync(rig, c.CharacterId, TenguDefensive, TenguDefensiveTwo);
        Assert.Empty(await rig.Ships.AssembleAsync(c.CharacterId, Rig.Station, [hull], [.. other, .. twice], null));

        Assert.False((await rig.Items.GetAsync(hull))!.Singleton);
    }

    [PgFact]
    public async Task A_plain_hull_is_assembled_without_subsystems_and_a_name_is_given_to_one_ship_only()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var hull = await rig.GiveAsync(c.CharacterId, Cockroach, singleton: false);

        var changed = await rig.Ships.AssembleAsync(c.CharacterId, Rig.Station, [hull], [], "Named");

        Assert.Equal(hull, Assert.Single(changed).ItemId);
        Assert.Empty(Assert.Single(changed).Items);
        Assert.True((await rig.Items.GetAsync(hull))!.Singleton);
    }

    [PgFact]
    public async Task A_fitted_strategic_cruiser_swaps_a_subsystem_in_the_station_by_the_fitting_rules()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync(shipTypeId: Tengu);
        int[] types = [TenguCore, TenguDefensive, TenguDefensiveTwo, TenguOffensive, TenguPropulsion];
        await rig.Statics.EnsureTypesAsync(types);
        foreach (var type in types)
        {
            foreach (var skill in rig.Statics.Type(type)!.RequiredSkills)
            {
                await rig.SetSkillAsync(c.CharacterId, skill, 5);
            }
        }
        var all = await TenguSubsystemsAsync(rig, c.CharacterId, types);
        var fitted = await rig.Ships.LoadAsync(c.CharacterId);
        Assert.NotNull(fitted);
        Assert.Equal(4, Slots.Count(fitted.World, c.ShipId, SlotFamily.Subsystem));
        Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(c.CharacterId, all[0], Rig.Station, c.ShipId, Core, null));
        Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(c.CharacterId, all[1], Rig.Station, c.ShipId, Defensive, null));
        Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(c.CharacterId, all[3], Rig.Station, c.ShipId, 127, null));
        Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(c.CharacterId, all[4], Rig.Station, c.ShipId, 128, null));

        // out of the defensive class, a second defensive in: the station swap of the reference
        Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(c.CharacterId, all[1], c.ShipId, Rig.Station, ItemFlags.Hangar, null));
        Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(c.CharacterId, all[2], Rig.Station, c.ShipId, Defensive, null));
        var contents = await rig.InShipAsync(c.CharacterId, c.ShipId);
        Assert.Contains((TenguDefensiveTwo, Defensive, ItemState.Offline), contents);

        // a Legion's defensive subsystem does not fit a Tengu
        var legion = await rig.GiveAsync(c.CharacterId, LegionDefensive);
        Assert.IsType<ShipRefused>(await rig.Ships.MoveAsync(c.CharacterId, legion, Rig.Station, c.ShipId, Defensive, null));
    }
}
