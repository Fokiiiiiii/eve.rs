using Eve.Dogma;
using Eve.Fitting;
using Eve.Inventory;

namespace Eve.Ships.Tests;

public class ShipTests
{
    private const int Online = (int)ItemState.Online;

    private static ShipChanged Changed(ShipOutcome outcome) => Assert.IsType<ShipChanged>(outcome);

    private static IEnumerable<string> Show(IEnumerable<DogmaEvent> events) => events.Select(e => e switch
    {
        AttributeChanged a => $"{a.ItemId} #{a.AttributeId}: {a.Old?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"} -> {a.New.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
        EffectChanged c => $"{c.ItemId} effect {c.EffectId} {(c.Started ? "starts" : "stops")}",
        _ => e.ToString()!,
    });

    [PgFact]
    public async Task A_new_character_has_its_starter_modules_online_in_their_slots()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        Assert.Equal(
            [(Rig.Afterburner, 19, ItemState.Online), (Rig.Railgun, 27, ItemState.Online), (Rig.Miner, 28, ItemState.Online)],
            await rig.InShipAsync(c.CharacterId, c.ShipId));
        var snapshot = (await rig.Ships.LoadAsync(c.CharacterId))!;
        Assert.Equal(70.6, snapshot.World.Get(c.ShipId, BuiltInEffects.CpuLoad), 9);
        Assert.Equal(9, snapshot.World.Get(c.ShipId, BuiltInEffects.PowerLoad), 9);
        Assert.True(snapshot.World.Get(c.ShipId, BuiltInEffects.TurretSlotsLeft) > 0);
    }

    [PgFact]
    public async Task Taking_a_module_out_of_its_slot_puts_it_in_the_hangar_offline_and_says_what_the_ship_lost()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var weapon = c.ShipId + 2;
        var outcome = Changed(await rig.Ships.MoveAsync(c.CharacterId, weapon, c.ShipId, Rig.Station, ItemFlags.Hangar, null));

        Assert.Equal(weapon, outcome.ItemId);
        var moved = Assert.Single(outcome.Items);
        Assert.Equal((Rig.Station, ItemFlags.Hangar, c.ShipId, 27), (moved.After.LocationId, moved.After.Flag, moved.PreviousLocationId, moved.PreviousFlag));
        // the ship's numbers, in the order of the attributes, then the module's, then the effect that stops
        Assert.Equal(
            [$"{c.ShipId} #15: 9 -> 3", $"{c.ShipId} #49: 70.6 -> 67", $"{c.ShipId} #102: 6 -> 7", $"{weapon} #2: 1 -> 0", $"{weapon} effect 16 stops"],
            Show(outcome.Events));
        Assert.Equal([(Rig.Afterburner, 19, ItemState.Online), (Rig.Miner, 28, ItemState.Online)], await rig.InShipAsync(c.CharacterId, c.ShipId));
        var stored = (await rig.Items.GetAsync(weapon))!;
        Assert.Equal((Rig.Station, ItemFlags.Hangar), (stored.LocationId, stored.Flag));
        Assert.Equal(ItemState.Offline, (await rig.Modules.StatesAsync([weapon]))[weapon]);
    }

    [PgFact]
    public async Task A_module_in_the_hangar_goes_in_the_slot_asked_for_and_is_fitted_offline()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var weapon = c.ShipId + 2;
        await rig.Ships.MoveAsync(c.CharacterId, weapon, c.ShipId, Rig.Station, ItemFlags.Hangar, null);

        var outcome = Changed(await rig.Ships.MoveAsync(c.CharacterId, weapon, Rig.Station, c.ShipId, 27, 1));
        Assert.Equal([$"{c.ShipId} #102: 7 -> 6"], Show(outcome.Events)); // a fitted turret uses a hard point; an offline module costs no processor or power
        var moved = Assert.Single(outcome.Items);
        Assert.Equal((c.ShipId, 27, Rig.Station, ItemFlags.Hangar), (moved.After.LocationId, moved.After.Flag, moved.PreviousLocationId, moved.PreviousFlag));
        Assert.Equal((Rig.Railgun, 27, ItemState.Offline), (await rig.InShipAsync(c.CharacterId, c.ShipId)).Single(m => m.Flag == 27));
    }

    [PgFact]
    public async Task A_module_asked_to_go_to_the_ship_without_a_slot_takes_the_first_that_suits()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var afterburner = c.ShipId + 1;
        await rig.Ships.MoveAsync(c.CharacterId, afterburner, c.ShipId, Rig.Station, ItemFlags.Hangar, null);
        await rig.Ships.MoveAsync(c.CharacterId, c.ShipId + 3, c.ShipId, Rig.Station, ItemFlags.Hangar, null); // the miner too: slot 28 is free

        var second = await rig.GiveAsync(c.CharacterId, Rig.Afterburner);
        // the first medium slot is free, so it is the one
        Assert.Equal(19, Changed(await rig.Ships.MoveAsync(c.CharacterId, second, Rig.Station, c.ShipId, null, null)).Items.Single().After.Flag);
        // a module that is no module goes to the cargo hold
        var goods = await rig.GiveAsync(c.CharacterId, 34, singleton: false, quantity: 50);
        Assert.Equal(CharacterShips.CargoFlag, Changed(await rig.Ships.MoveAsync(c.CharacterId, goods, Rig.Station, c.ShipId, null, 50)).Stacks!.Single().After.Flag);
    }

    [PgFact]
    public async Task What_the_rules_forbid_is_refused_and_leaves_everything_where_it_was()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var third = await rig.GiveAsync(c.CharacterId, Rig.Railgun);

        // the slot is taken
        var occupied = Assert.IsType<ShipRefused>(await rig.Ships.MoveAsync(c.CharacterId, third, Rig.Station, c.ShipId, 27, 1));
        Assert.Equal((ShipRefusal.CannotFit, FitError.SlotOccupied), (occupied.Reason, occupied.Fit!.Error));
        // a slot of another kind
        var wrongKind = Assert.IsType<ShipRefused>(await rig.Ships.MoveAsync(c.CharacterId, third, Rig.Station, c.ShipId, 19, 1));
        Assert.Equal(FitError.InvalidSlot, wrongKind.Fit!.Error);
        var lowSlot = Assert.IsType<ShipRefused>(await rig.Ships.MoveAsync(c.CharacterId, third, Rig.Station, c.ShipId, 11, 1));
        Assert.Equal(FitError.InvalidSlot, lowSlot.Fit!.Error);
        // the flag is not a place a ship has, and the drone bay is no place for a module
        Assert.IsType<NothingHappened>(await rig.Ships.MoveAsync(c.CharacterId, third, Rig.Station, c.ShipId, 4000, 1));
        Assert.Equal(ShipRefusal.NotADrone, Assert.IsType<ShipRefused>(await rig.Ships.MoveAsync(c.CharacterId, third, Rig.Station, c.ShipId, 87, 1)).Reason);

        Assert.Equal(3, (await rig.InShipAsync(c.CharacterId, c.ShipId)).Count);
        Assert.Equal(Rig.Station, (await rig.Items.GetAsync(third))!.LocationId);
    }

    [PgFact]
    public async Task A_module_needs_the_skills_it_names_and_the_message_names_the_skill()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var control = await rig.GiveAsync(c.CharacterId, Rig.DamageControlII); // needs Hull Upgrades at 4, the character has less
        var refused = Assert.IsType<ShipRefused>(await rig.Ships.MoveAsync(c.CharacterId, control, Rig.Station, c.ShipId, 11, 1));
        Assert.Equal(FitError.SkillRequired, refused.Fit!.Error);
        Assert.Equal((3394, 4), (refused.Fit.Data["skillTypeID"], refused.Fit.Data["requiredLevel"]));
        Assert.False(string.IsNullOrEmpty(rig.Statics.Type(3394)?.Name)); // the static data can name the skill for the message

        var easy = await rig.GiveAsync(c.CharacterId, Rig.DamageControlI);
        Changed(await rig.Ships.MoveAsync(c.CharacterId, easy, Rig.Station, c.ShipId, 11, 1));
        // only one damage control may be fitted
        var second = await rig.GiveAsync(c.CharacterId, Rig.DamageControlI);
        var many = Assert.IsType<ShipRefused>(await rig.Ships.MoveAsync(c.CharacterId, second, Rig.Station, c.ShipId, 12, 1));
        Assert.Equal(FitError.MaxGroupFitted, many.Fit!.Error);
    }

    [PgFact]
    public async Task Things_that_are_not_there_or_not_the_characters_are_not_done_at_all()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var other = await rig.NewCharacterAsync("Someone Else", account: 2);
        var weapon = c.ShipId + 2;

        Assert.IsType<NothingHappened>(await rig.Ships.MoveAsync(c.CharacterId, 999_999_999, Rig.Station, Rig.Station, null, null));   // no such item
        Assert.IsType<NothingHappened>(await rig.Ships.MoveAsync(other.CharacterId, weapon, c.ShipId, Rig.Station, 4, null));           // someone else's
        Assert.IsType<NothingHappened>(await rig.Ships.MoveAsync(c.CharacterId, weapon, 12345, Rig.Station, 4, null));                   // not where it was said to be
        Assert.IsType<NothingHappened>(await rig.Ships.MoveAsync(c.CharacterId, c.ShipId, Rig.Station, c.ShipId, null, null));          // a ship into itself
        Assert.IsType<NothingHappened>(await rig.Ships.MoveAsync(c.CharacterId, weapon, c.ShipId, 60_000_002, 4, null));                 // to a station it is not in
        Assert.IsType<NothingHappened>(await rig.Ships.MoveAsync(c.CharacterId, weapon, c.ShipId, Rig.Station, 5, null));                // the hangar has no such flag
        Assert.Equal(3, (await rig.InShipAsync(c.CharacterId, c.ShipId)).Count);
    }

    [PgFact]
    public async Task A_stack_is_moved_whole_or_in_part_and_goods_tell_the_stacks_that_changed()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var goods = await rig.GiveAsync(c.CharacterId, 34, singleton: false, quantity: 50);
        // part of it: the rest stays where it was, and what goes is a stack of its own (see CargoTests)
        var part = Changed(await rig.Ships.MoveAsync(c.CharacterId, goods, Rig.Station, c.ShipId, 5, 20));
        Assert.Equal(Rig.Station, (await rig.Items.GetAsync(goods))!.LocationId);
        Assert.Equal(30, (await rig.Items.GetAsync(goods))!.Quantity);
        Assert.Equal(c.ShipId, (await rig.Items.GetAsync(part.ItemId))!.LocationId);
        var moved = Changed(await rig.Ships.MoveAsync(c.CharacterId, goods, Rig.Station, c.ShipId, 5, 30));
        Assert.Equal(c.ShipId, moved.Stacks!.Single().After.LocationId);
        Assert.Empty(moved.Items); // goods are not a fitting move: the client is told the stacks, not the items
        Assert.Empty(moved.Events); // goods change nothing about the ship's numbers
        // and back into the hangar
        Assert.Equal(Rig.Station, Changed(await rig.Ships.MoveAsync(c.CharacterId, goods, c.ShipId, Rig.Station, 4, null)).Stacks!.Single().After.LocationId);
    }

    [PgFact]
    public async Task Switching_a_module_offline_and_online_says_what_the_ship_gains_and_loses_and_keeps_the_state()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var afterburner = c.ShipId + 1;

        var off = Changed(await rig.Ships.SetOnlineAsync(c.CharacterId, c.ShipId, afterburner, online: false));
        Assert.Equal([$"{afterburner} #2: 1 -> 0", $"{c.ShipId} #15: 9 -> 8", $"{c.ShipId} #49: 70.6 -> 68.6", $"{afterburner} effect 16 stops"], Show(off.Events));
        Assert.Equal(ItemState.Offline, (await rig.Modules.StatesAsync([afterburner]))[afterburner]);

        var on = Changed(await rig.Ships.SetOnlineAsync(c.CharacterId, c.ShipId, afterburner, online: true));
        Assert.Equal([$"{afterburner} #2: 0 -> 1", $"{c.ShipId} #15: 8 -> 9", $"{c.ShipId} #49: 68.6 -> 70.6", $"{afterburner} effect 16 starts"], Show(on.Events));
        Assert.Equal(ItemState.Online, (await rig.Modules.StatesAsync([afterburner]))[afterburner]);

        // asking again for what is: only the effect is repeated
        var again = Changed(await rig.Ships.SetOnlineAsync(c.CharacterId, c.ShipId, afterburner, online: true));
        Assert.Equal([$"{afterburner} effect 16 starts"], Show(again.Events));
        // switching off what is off says nothing
        await rig.Ships.SetOnlineAsync(c.CharacterId, c.ShipId, afterburner, online: false);
        Assert.Empty(Changed(await rig.Ships.SetOnlineAsync(c.CharacterId, c.ShipId, afterburner, online: false)).Events);
    }

    [PgFact]
    public async Task Cockroach_modules_are_not_blocked_by_the_rookie_hull_cpu_limit()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        await rig.SetSkillAsync(c.CharacterId, 3426, 0);
        await rig.Ships.MoveAsync(c.CharacterId, c.ShipId + 2, c.ShipId, Rig.Station, ItemFlags.Hangar, null);
        var miner = await rig.GiveAsync(c.CharacterId, Rig.Miner);
        Changed(await rig.Ships.MoveAsync(c.CharacterId, miner, Rig.Station, c.ShipId, 27, 1));
        Changed(await rig.Ships.SetOnlineAsync(c.CharacterId, c.ShipId, miner, online: true));
        var control = await rig.GiveAsync(c.CharacterId, Rig.DamageControlI);
        Changed(await rig.Ships.MoveAsync(c.CharacterId, control, Rig.Station, c.ShipId, 11, 1));

        Changed(await rig.Ships.SetOnlineAsync(c.CharacterId, c.ShipId, control, online: true));
        Assert.Equal(ItemState.Online, (await rig.Modules.StatesAsync([control]))[control]);
    }

    [PgFact]
    public async Task Cockroach_modules_are_not_blocked_by_the_rookie_hull_power_grid_limit()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        await rig.SetSkillAsync(c.CharacterId, 3425, 1);
        var extender = await rig.GiveAsync(c.CharacterId, 3829);
        Changed(await rig.Ships.MoveAsync(c.CharacterId, extender, Rig.Station, c.ShipId, Slots.MediumFirst + 1, 1));

        Changed(await rig.Ships.SetOnlineAsync(c.CharacterId, c.ShipId, extender, online: true));
        Assert.Equal(ItemState.Online, (await rig.Modules.StatesAsync([extender]))[extender]);
    }

    [PgFact]
    public async Task A_module_that_is_not_fitted_to_the_ship_cannot_be_switched()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var loose = await rig.GiveAsync(c.CharacterId, Rig.Afterburner);
        Assert.Equal(ShipRefusal.NotFitted, Assert.IsType<ShipRefused>(await rig.Ships.SetOnlineAsync(c.CharacterId, c.ShipId, loose, online: true)).Reason);
        Assert.Equal(ShipRefusal.NotFitted, Assert.IsType<ShipRefused>(await rig.Ships.SetOnlineAsync(c.CharacterId, 12345, c.ShipId + 1, online: true)).Reason);
        Assert.Equal(ShipRefusal.NotFitted, Assert.IsType<ShipRefused>(await rig.Ships.SetOnlineAsync(999, c.ShipId, c.ShipId + 1, online: true)).Reason);
    }

    [PgFact]
    public async Task One_characters_changes_happen_one_after_the_other()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var weapon = c.ShipId + 2;
        // the same module taken out twice at once: one of them moves it, the other finds nothing to do or the same result
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => rig.Ships.MoveAsync(c.CharacterId, weapon, c.ShipId, Rig.Station, ItemFlags.Hangar, null)));
        Assert.Equal(1, results.Count(r => r is ShipChanged { Events.Count: > 0 }));
        Assert.Equal(2, (await rig.InShipAsync(c.CharacterId, c.ShipId)).Count);
    }

    [PgFact]
    public async Task What_another_character_owns_is_not_moved_even_when_it_lies_in_the_same_hangar()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var other = await rig.NewCharacterAsync("Someone Else", account: 2);
        var theirs = await rig.GiveAsync(other.CharacterId, Rig.Miner); // in the same station's hangar
        Assert.IsType<NothingHappened>(await rig.Ships.MoveAsync(c.CharacterId, theirs, Rig.Station, c.ShipId, 28, 1));
        Assert.Equal(Rig.Station, (await rig.Items.GetAsync(theirs))!.LocationId);
    }

    [PgFact]
    public async Task A_rig_cannot_be_taken_out_of_the_ship()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var fitted = await rig.GiveAsync(c.CharacterId, 31788, location: c.ShipId, flag: Slots.RigFirst); // a Small Core Defense Field Extender I
        Assert.IsType<NothingHappened>(await rig.Ships.MoveAsync(c.CharacterId, fitted, c.ShipId, Rig.Station, ItemFlags.Hangar, null));
        Assert.Equal((c.ShipId, Slots.RigFirst), ((await rig.Items.GetAsync(fitted))!.LocationId, (await rig.Items.GetAsync(fitted))!.Flag));
    }

    [PgFact]
    public async Task A_module_moved_to_another_slot_of_the_ship_keeps_its_state()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var afterburner = c.ShipId + 1; // online in the first medium slot
        Changed(await rig.Ships.MoveAsync(c.CharacterId, afterburner, c.ShipId, c.ShipId, Slots.MediumFirst + 1, 1));
        Assert.Equal((Rig.Afterburner, Slots.MediumFirst + 1, ItemState.Online), (await rig.InShipAsync(c.CharacterId, c.ShipId)).Single(m => m.Type == Rig.Afterburner));
    }

    [PgFact]
    public async Task Taking_an_offline_module_out_still_says_that_its_online_effect_stops_and_nothing_else_about_it()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var afterburner = c.ShipId + 1;
        await rig.Ships.SetOnlineAsync(c.CharacterId, c.ShipId, afterburner, online: false);
        var outcome = Changed(await rig.Ships.MoveAsync(c.CharacterId, afterburner, c.ShipId, Rig.Station, ItemFlags.Hangar, null));
        Assert.Equal([$"{afterburner} effect 16 stops"], Show(outcome.Events)); // no attribute of the ship or the module changes: it was off already
    }
}

