using Eve.Characters;
using Eve.Dogma;
using Eve.Inventory;

namespace Eve.Ships.Tests;

/// <summary>Ammunition in a docked ship: what a module takes, what goes back, and the rounds a turret in space has spent (the rules of the reference's LoadAmmo and UnloadAmmo).</summary>
public class AmmoTests
{
    private const int RailgunI = 10678, Antimatter = 222, Iron = 215, MultifrequencyCrystal = 262;
    private const int Slot = 28;

    /// <summary>A character whose miner is out and whose slot holds a 125mm Railgun I (offline), with 500 antimatter in the hangar.</summary>
    private static async Task<(Created Character, long Gun, long Stack)> ArmedAsync(Rig rig)
    {
        var c = await rig.NewCharacterAsync();
        Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(c.CharacterId, c.ShipId + 3, c.ShipId, Rig.Station, ItemFlags.Hangar, null));
        var gun = await rig.GiveAsync(c.CharacterId, RailgunI);
        Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(c.CharacterId, gun, Rig.Station, c.ShipId, Slot, 1));
        var stack = await rig.GiveAsync(c.CharacterId, Antimatter, singleton: false, quantity: 500);
        return (c, gun, stack);
    }

    private static async Task<Item?> ChargeAsync(Rig rig, Created c) =>
        (await rig.Items.InLocationAsync(c.ShipId, c.CharacterId)).SingleOrDefault(i => i.Flag == Slot && i.TypeId != RailgunI);

    private static async Task<long> HangarOfAsync(Rig rig, Created c, int type) =>
        (await rig.Items.InLocationAsync(Rig.Station, c.CharacterId)).Where(i => i.TypeId == type && i.Flag == ItemFlags.Hangar).Sum(i => i.Quantity);

    [PgFact]
    public async Task A_gun_takes_what_its_capacity_holds_of_a_stack_and_the_client_is_told_in_the_reference_order()
    {
        await using var rig = await Rig.CreateAsync();
        var (c, gun, stack) = await ArmedAsync(rig);

        var changed = Assert.IsType<AmmoChanged>(await rig.Ships.LoadAmmoAsync(c.CharacterId, c.ShipId, [gun], [stack], Rig.Station));

        // the gun holds its capacity over the volume of a round (0.2 / 0.0025); the rest of the stack stays in the hangar
        var charge = Assert.IsType<Item>(await ChargeAsync(rig, c));
        Assert.Equal((Antimatter, 80L), (charge.TypeId, charge.Quantity));
        Assert.Equal(420, await HangarOfAsync(rig, c, Antimatter));
        Assert.Equal(
            ["ChargeBeingLoaded", "ItemChanged", "ChargePrimed", "ChargeQuantityChanged", "ModuleAttributesChanged", "FittingRefreshed"],
            changed.Events.Select(e => e.GetType().Name));
        var loading = Assert.IsType<ChargeBeingLoaded>(changed.Events[0]);
        Assert.Equal([gun], loading.ModuleIds);
        Assert.Equal((Antimatter, 5000), (loading.ChargeTypeId, loading.ReloadMs));
        Assert.Equal(new ChargeQuantityChanged(c.ShipId, Slot, Antimatter, 80, 0), changed.Events[3]);
        // antimatter halves the range of the gun (its weapon range multiplier is 0.5)
        var range = Assert.Single(Assert.IsType<ModuleAttributesChanged>(changed.Events[4]).Changes, a => a.AttributeId == 54);
        Assert.Equal(0.5, range.New / range.Old!.Value, 9);
    }

    [PgFact]
    public async Task A_gun_loaded_again_takes_nothing_and_a_gun_unloaded_gives_its_rounds_back_to_the_hangar()
    {
        await using var rig = await Rig.CreateAsync();
        var (c, gun, stack) = await ArmedAsync(rig);
        await rig.Ships.LoadAmmoAsync(c.CharacterId, c.ShipId, [gun], [stack], Rig.Station);

        var again = Assert.IsType<AmmoChanged>(await rig.Ships.LoadAmmoAsync(c.CharacterId, c.ShipId, [gun], [stack], Rig.Station));
        Assert.Empty(again.Events);
        Assert.Equal(420, await HangarOfAsync(rig, c, Antimatter));

        var unloaded = Assert.IsType<AmmoChanged>(await rig.Ships.UnloadAmmoAsync(c.CharacterId, c.ShipId, [gun], Rig.Station, null, null));
        Assert.Null(await ChargeAsync(rig, c));
        Assert.Equal(500, await HangarOfAsync(rig, c, Antimatter));
        Assert.Contains(unloaded.Events, e => e is ChargeQuantityChanged { New: 0, Old: 80 });
    }

    [PgFact]
    public async Task Another_kind_sends_the_first_back_and_a_charge_the_gun_does_not_take_is_left_alone()
    {
        await using var rig = await Rig.CreateAsync();
        var (c, gun, stack) = await ArmedAsync(rig);
        await rig.Ships.LoadAmmoAsync(c.CharacterId, c.ShipId, [gun], [stack], Rig.Station);
        var crystal = await rig.GiveAsync(c.CharacterId, MultifrequencyCrystal, singleton: false, quantity: 10);

        // a laser crystal is of no group the railgun names
        var refused = Assert.IsType<AmmoChanged>(await rig.Ships.LoadAmmoAsync(c.CharacterId, c.ShipId, [gun], [crystal], Rig.Station));
        Assert.Empty(refused.Events);
        Assert.Equal(Antimatter, (await ChargeAsync(rig, c))!.TypeId);

        var iron = await rig.GiveAsync(c.CharacterId, Iron, singleton: false, quantity: 3);
        Assert.IsType<AmmoChanged>(await rig.Ships.LoadAmmoAsync(c.CharacterId, c.ShipId, [gun], [iron], Rig.Station));
        var loaded = Assert.IsType<Item>(await ChargeAsync(rig, c));
        Assert.Equal((Iron, 3L), (loaded.TypeId, loaded.Quantity));
        Assert.Equal(500, await HangarOfAsync(rig, c, Antimatter)); // the antimatter went back whole
    }

    [PgFact]
    public async Task Rounds_spent_in_space_are_kept_and_never_given_back_and_an_empty_slot_loses_its_charge()
    {
        await using var rig = await Rig.CreateAsync();
        var (c, gun, stack) = await ArmedAsync(rig);
        await rig.Ships.LoadAmmoAsync(c.CharacterId, c.ShipId, [gun], [stack], Rig.Station);

        await rig.Ships.SpendChargeAsync(c.CharacterId, gun, 79);
        Assert.Equal(79, (await ChargeAsync(rig, c))!.Quantity);
        await rig.Ships.SpendChargeAsync(c.CharacterId, gun, 80); // a late answer does not give a round back
        Assert.Equal(79, (await ChargeAsync(rig, c))!.Quantity);
        await rig.Ships.SpendChargeAsync(c.CharacterId, gun, 0);
        Assert.Null(await ChargeAsync(rig, c));
        // another character's module is nothing of this one's
        await rig.Ships.SpendChargeAsync(c.CharacterId + 1, gun, 0);
    }

    [PgFact]
    public async Task The_charge_in_a_gun_is_not_a_fitted_module_and_the_ship_still_counts_its_slots_as_before()
    {
        await using var rig = await Rig.CreateAsync();
        var (c, gun, stack) = await ArmedAsync(rig);
        var before = (await rig.Ships.LoadAsync(c.CharacterId))!;
        await rig.Ships.LoadAmmoAsync(c.CharacterId, c.ShipId, [gun], [stack], Rig.Station);

        var after = (await rig.Ships.LoadAsync(c.CharacterId))!;
        Assert.Equal(before.Fitted.Select(m => m.Id), after.Fitted.Select(m => m.Id));
        Assert.Equal((Antimatter, Slot), (after.ChargeOf(after.Fitted.Single(m => m.TypeId == RailgunI))!.TypeId, after.Charges.Single().Flag));
        Assert.Equal(80, after.World.Get(after.Charges.Single().Id, ShipSnapshot.QuantityCharge));
    }
}
