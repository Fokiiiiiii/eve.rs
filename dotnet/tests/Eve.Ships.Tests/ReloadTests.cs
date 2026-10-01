using Eve.Characters;
using Eve.Inventory;

namespace Eve.Ships.Tests;

/// <summary>
/// Ammunition in a ship in space: the rounds come from the hold, a load takes the module's reload time (planned, then done), an unload takes none, and a gun that runs has its
/// rounds in the simulation (the rules and the order are the reference's: the recording <c>reload.json</c>).
/// </summary>
public class ReloadTests
{
    private const int RailgunI = 10678, Antimatter = 222, Iron = 215;
    private const int Slot = 28, Cargo = 5;

    /// <summary>A pilot in space whose slot holds a 125mm Railgun I (offline) and whose hold has 200 antimatter.</summary>
    private static async Task<(Created Character, long Gun, long Stack)> InSpaceArmedAsync(Rig rig, long inHold = 200)
    {
        var c = await rig.NewCharacterAsync();
        Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(c.CharacterId, c.ShipId + 3, c.ShipId, Rig.Station, ItemFlags.Hangar, null));
        var gun = await rig.GiveAsync(c.CharacterId, RailgunI);
        Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(c.CharacterId, gun, Rig.Station, c.ShipId, Slot, 1));
        var stack = inHold > 0 ? await PutAsync(rig, c.CharacterId, c.ShipId, Cargo, Antimatter, inHold) : 0;
        Assert.NotNull((await rig.Ships.UndockAsync(c.CharacterId)).Moved);
        return (c, gun, stack);
    }

    private static async Task<long> PutAsync(Rig rig, long owner, long place, int flag, int type, long quantity)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await rig.Items.CreateAsync(connection, transaction, new NewItem(type, owner, place, flag, quantity));
        await transaction.CommitAsync();
        return id;
    }

    private static async Task<Item?> ChargeAsync(Rig rig, Created c) =>
        (await rig.Items.InLocationAsync(c.ShipId, c.CharacterId)).SingleOrDefault(i => i.Flag == Slot && i.TypeId != RailgunI);

    private static async Task<long> HoldAsync(Rig rig, Created c, int type) =>
        (await rig.Items.InLocationAsync(c.ShipId, c.CharacterId)).Where(i => i.TypeId == type && i.Flag == Cargo).Sum(i => i.Quantity);

    [PgFact]
    public async Task A_pilot_who_loads_a_gun_in_space_is_planned_a_reload_of_the_modules_time_and_nothing_moves_yet()
    {
        await using var rig = await Rig.CreateAsync();
        var (c, gun, stack) = await InSpaceArmedAsync(rig);

        var plan = Assert.Single(await rig.Ships.PlanReloadsAsync(c.CharacterId, c.ShipId, [gun], [stack], c.ShipId));

        Assert.Equal((c.ShipId, gun, RailgunI, Slot, Antimatter, 5000), (plan.ShipId, plan.ModuleId, plan.ModuleTypeId, plan.ModuleFlag, plan.ChargeTypeId, plan.ReloadMs));
        Assert.Equal([stack], plan.SourceItemIds);
        Assert.Null(await ChargeAsync(rig, c));
        Assert.Equal(200, await HoldAsync(rig, c, Antimatter));
        // nothing is named and the gun is empty: there is no kind to load (the reference skips it); a gun with rounds in it tops them up from every stack of the kind
        Assert.Empty(await rig.Ships.PlanReloadsAsync(c.CharacterId, c.ShipId, [gun], [], c.ShipId));
        await PutAsync(rig, c.CharacterId, c.ShipId, Slot, Antimatter, 10);
        var other = await PutAsync(rig, c.CharacterId, c.ShipId, Cargo, Antimatter, 5);
        Assert.Equal([stack, other], Assert.Single(await rig.Ships.PlanReloadsAsync(c.CharacterId, c.ShipId, [gun], [], c.ShipId)).SourceItemIds);
    }

    [PgFact]
    public async Task A_reload_is_not_planned_for_a_module_that_is_full_one_with_nothing_to_take_or_from_a_place_that_is_not_the_hold()
    {
        await using var rig = await Rig.CreateAsync();
        var (c, gun, stack) = await InSpaceArmedAsync(rig);
        await PutAsync(rig, c.CharacterId, c.ShipId, Slot, Antimatter, 80);

        Assert.Empty(await rig.Ships.PlanReloadsAsync(c.CharacterId, c.ShipId, [gun], [stack], c.ShipId)); // full
        Assert.Empty(await rig.Ships.PlanReloadsAsync(c.CharacterId, c.ShipId, [gun], [stack], Rig.Station)); // not the hold
        Assert.Empty(await rig.Ships.PlanReloadsAsync(c.CharacterId, c.ShipId, [gun + 999], [stack], c.ShipId)); // not a module of the ship

        await using var empty = await Rig.CreateAsync();
        var (e, emptyGun, _) = await InSpaceArmedAsync(empty, inHold: 0);
        Assert.Empty(await empty.Ships.PlanReloadsAsync(e.CharacterId, e.ShipId, [emptyGun], [], e.ShipId)); // nothing to take
    }

    [PgFact]
    public async Task A_reload_that_has_run_loads_the_gun_from_the_hold_and_the_client_is_told_in_the_order_of_the_recording()
    {
        await using var rig = await Rig.CreateAsync();
        var (c, gun, stack) = await InSpaceArmedAsync(rig);
        var plan = Assert.Single(await rig.Ships.PlanReloadsAsync(c.CharacterId, c.ShipId, [gun], [stack], c.ShipId));

        var done = Assert.IsType<Reloaded>(await rig.Ships.CompleteReloadAsync(c.CharacterId, plan, GunRounds.Idle));

        var charge = Assert.IsType<Item>(await ChargeAsync(rig, c));
        Assert.Equal((Antimatter, 80L), (charge.TypeId, charge.Quantity));
        Assert.Equal(120, await HoldAsync(rig, c, Antimatter));
        Assert.Equal(0, done.Shortfall);
        // the reload was told when it began: here the stack that got smaller, the charge that came and what it did to the gun (a ship in space is not told its fitting again)
        Assert.Equal(["ItemChanged", "ChargePrimed", "ChargeQuantityChanged", "ModuleAttributesChanged"], done.Changed.Events.Select(e => e.GetType().Name));
        var smaller = Assert.IsType<ItemChanged>(done.Changed.Events[0]);
        Assert.Equal((stack, 120L, 200L, c.ShipId, Cargo), (smaller.After.Id, smaller.After.Quantity, smaller.PreviousQuantity, smaller.PreviousLocationId, smaller.PreviousFlag));
        Assert.Equal(new ChargeQuantityChanged(c.ShipId, Slot, Antimatter, 80, 0), done.Changed.Events[2]);
    }

    [PgFact]
    public async Task A_reload_that_has_run_after_the_rounds_were_taken_out_loads_what_the_gun_lacks_and_one_that_has_none_to_take_loads_nothing()
    {
        await using var rig = await Rig.CreateAsync();
        var (c, gun, stack) = await InSpaceArmedAsync(rig, inHold: 40);
        await PutAsync(rig, c.CharacterId, c.ShipId, Slot, Antimatter, 5);
        var plan = Assert.Single(await rig.Ships.PlanReloadsAsync(c.CharacterId, c.ShipId, [gun], [stack], c.ShipId));

        var done = Assert.IsType<Reloaded>(await rig.Ships.CompleteReloadAsync(c.CharacterId, plan, GunRounds.Idle));

        // the gun lacked 75 and the hold has 40: it holds 45 and the hold's stack is gone, with no new charge told (the kind is the one that was in it)
        Assert.Equal(45, (await ChargeAsync(rig, c))!.Quantity);
        Assert.Equal(0, await HoldAsync(rig, c, Antimatter));
        Assert.Equal(["ItemChanged", "ChargeQuantityChanged"], done.Changed.Events.Select(e => e.GetType().Name));
        Assert.Equal(new ChargeQuantityChanged(c.ShipId, Slot, Antimatter, 45, 5), done.Changed.Events[1]);
        Assert.Empty(Assert.IsType<AmmoChanged>(Assert.IsType<Reloaded>(await rig.Ships.CompleteReloadAsync(c.CharacterId, plan, GunRounds.Idle)).Changed).Events);
    }

    [PgFact]
    public async Task A_gun_that_runs_is_given_the_rounds_the_simulation_added_and_the_hold_that_is_short_is_told_to_make_it_up()
    {
        await using var rig = await Rig.CreateAsync();
        var (c, gun, stack) = await InSpaceArmedAsync(rig, inHold: 200);
        var charge = await PutAsync(rig, c.CharacterId, c.ShipId, Slot, Antimatter, 3);
        var plan = Assert.Single(await rig.Ships.PlanReloadsAsync(c.CharacterId, c.ShipId, [gun], [stack], c.ShipId));

        // the simulation added 77 rounds to the 3 that were in it
        var done = Assert.IsType<Reloaded>(await rig.Ships.CompleteReloadAsync(c.CharacterId, plan, new GunRounds(true, 3, 80)));
        Assert.Equal((80L, 0L), ((await ChargeAsync(rig, c))!.Quantity, done.Shortfall));
        Assert.Equal(123, await HoldAsync(rig, c, Antimatter));
        Assert.Equal(["ItemChanged", "ChargeQuantityChanged"], done.Changed.Events.Select(e => e.GetType().Name));
        Assert.Equal(new ChargeQuantityChanged(c.ShipId, Slot, Antimatter, 80, 3), done.Changed.Events[1]);
        Assert.Equal(charge, (await ChargeAsync(rig, c))!.Id);

        // the hold has 10 and the simulation added 70: the gun has what there was, and 60 are given back
        await using var poor = await Rig.CreateAsync();
        var (p, poorGun, poorStack) = await InSpaceArmedAsync(poor, inHold: 10);
        await PutAsync(poor, p.CharacterId, p.ShipId, Slot, Antimatter, 10);
        var poorPlan = Assert.Single(await poor.Ships.PlanReloadsAsync(p.CharacterId, p.ShipId, [poorGun], [poorStack], p.ShipId));
        var short_ = Assert.IsType<Reloaded>(await poor.Ships.CompleteReloadAsync(p.CharacterId, poorPlan, new GunRounds(true, 10, 80)));
        Assert.Equal((20L, 60L), ((await ChargeAsync(poor, p))!.Quantity, short_.Shortfall));
    }

    [PgFact]
    public async Task A_gun_that_runs_another_kind_keeps_it_and_a_gun_that_does_not_gives_it_back_to_the_hold_first()
    {
        await using var rig = await Rig.CreateAsync();
        var (c, gun, stack) = await InSpaceArmedAsync(rig);
        await PutAsync(rig, c.CharacterId, c.ShipId, Slot, Iron, 30);
        var plan = Assert.Single(await rig.Ships.PlanReloadsAsync(c.CharacterId, c.ShipId, [gun], [stack], c.ShipId));

        Assert.IsType<NothingHappened>(await rig.Ships.CompleteReloadAsync(c.CharacterId, plan, new GunRounds(true, 30, 30)));
        Assert.Equal((Iron, 30L), ((await ChargeAsync(rig, c))!.TypeId, (await ChargeAsync(rig, c))!.Quantity));

        Assert.IsType<Reloaded>(await rig.Ships.CompleteReloadAsync(c.CharacterId, plan, GunRounds.Idle));
        Assert.Equal((Antimatter, 80L), ((await ChargeAsync(rig, c))!.TypeId, (await ChargeAsync(rig, c))!.Quantity));
        Assert.Equal(30, await HoldAsync(rig, c, Iron));
    }

    [PgFact]
    public async Task A_reload_whose_module_is_no_longer_fitted_does_nothing()
    {
        await using var rig = await Rig.CreateAsync();
        var (c, gun, stack) = await InSpaceArmedAsync(rig);
        var plan = Assert.Single(await rig.Ships.PlanReloadsAsync(c.CharacterId, c.ShipId, [gun], [stack], c.ShipId));
        await rig.Ships.DeleteItemAsync(c.CharacterId, gun);

        Assert.IsType<NothingHappened>(await rig.Ships.CompleteReloadAsync(c.CharacterId, plan, GunRounds.Idle));
        Assert.Equal(200, await HoldAsync(rig, c, Antimatter));
    }

    [PgFact]
    public async Task What_a_pilot_unloads_in_space_goes_to_the_hold_at_once_and_a_gun_that_runs_gives_the_rounds_the_simulation_took()
    {
        await using var rig = await Rig.CreateAsync();
        var (c, gun, _) = await InSpaceArmedAsync(rig, inHold: 40);
        await PutAsync(rig, c.CharacterId, c.ShipId, Slot, Antimatter, 80);

        // 75 of the 80 rounds, the gun does not run: the stack of the hold is told first, then the rounds that are in the slot
        var part = Assert.IsType<AmmoChanged>(await rig.Ships.UnloadAmmoInSpaceAsync(c.CharacterId, c.ShipId, gun, c.ShipId, null, 75, GunRounds.Idle));
        Assert.Equal(["ItemChanged", "ChargeQuantityChanged"], part.Events.Select(e => e.GetType().Name));
        Assert.Equal((5L, 115L), ((await ChargeAsync(rig, c))!.Quantity, await HoldAsync(rig, c, Antimatter)));
        Assert.Equal(new ChargeQuantityChanged(c.ShipId, Slot, Antimatter, 5, 80), part.Events[1]);

        // the gun runs: the simulation took 5 rounds out of the 5 (the pilot asked for all of them)
        var all = Assert.IsType<AmmoChanged>(await rig.Ships.UnloadAmmoInSpaceAsync(c.CharacterId, c.ShipId, gun, c.ShipId, null, null, new GunRounds(true, 5, 0)));
        Assert.Contains(all.Events, e => e is ChargeQuantityChanged { New: 0, Old: 5 });
        Assert.Null(await ChargeAsync(rig, c));
        Assert.Equal(120, await HoldAsync(rig, c, Antimatter));

        // a gun that runs and had no rounds to give gives none
        await PutAsync(rig, c.CharacterId, c.ShipId, Slot, Antimatter, 7);
        Assert.Empty(Assert.IsType<AmmoChanged>(await rig.Ships.UnloadAmmoInSpaceAsync(c.CharacterId, c.ShipId, gun, c.ShipId, null, null, new GunRounds(true, 0, 0))).Events);
        Assert.Equal(7, (await ChargeAsync(rig, c))!.Quantity);
    }

    [PgFact]
    public async Task A_ship_in_space_is_not_served_by_the_calls_for_a_station_and_a_ship_in_a_station_is_not_planned_a_reload()
    {
        await using var rig = await Rig.CreateAsync();
        var (c, gun, stack) = await InSpaceArmedAsync(rig);

        Assert.IsType<NothingHappened>(await rig.Ships.LoadAmmoAsync(c.CharacterId, c.ShipId, [gun], [stack], c.ShipId));
        Assert.IsType<NothingHappened>(await rig.Ships.UnloadAmmoAsync(c.CharacterId, c.ShipId, [gun], c.ShipId, null, null));
        Assert.Null(await ChargeAsync(rig, c));

        await using var docked = await Rig.CreateAsync();
        var d = await docked.NewCharacterAsync();
        Assert.IsType<ShipChanged>(await docked.Ships.MoveAsync(d.CharacterId, d.ShipId + 3, d.ShipId, Rig.Station, ItemFlags.Hangar, null));
        var dockedGun = await docked.GiveAsync(d.CharacterId, RailgunI);
        Assert.IsType<ShipChanged>(await docked.Ships.MoveAsync(d.CharacterId, dockedGun, Rig.Station, d.ShipId, Slot, 1));
        await PutAsync(docked, d.CharacterId, d.ShipId, Cargo, Antimatter, 50);
        Assert.Empty(await docked.Ships.PlanReloadsAsync(d.CharacterId, d.ShipId, [dockedGun], [], d.ShipId));
    }
}
