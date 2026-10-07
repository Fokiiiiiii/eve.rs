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


    [PgFact]
    public async Task Item_moves_notify_the_ship_contract_hook_for_owner_changes_and_corporation_hangars()
    {
        var hook = new RecordingShipInsuranceInvalidator();
        await using var rig = await Rig.CreateAsync(hook);
        var first = await rig.NewCharacterAsync();
        var second = await rig.NewCharacterAsync();
        var shipId = await rig.GiveAsync(first.CharacterId, Rig.Cockroach);

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Items.MoveToOwnerAsync(connection, transaction, shipId, second.CharacterId, Rig.Station, ItemFlags.Hangar);
            await transaction.CommitAsync();
        }

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Items.MoveAsync(connection, transaction, shipId, Rig.Station + 1, ItemFlags.CorpSAG1);
            await transaction.CommitAsync();
        }

        Assert.Equal(
            [ShipContractInvalidationReason.OwnerChanged, ShipContractInvalidationReason.CorporationHangar],
            hook.Events.Select(e => e.Reason));
        Assert.All(hook.Events, e => Assert.Equal(shipId, e.ItemId));
        Assert.All(hook.Events, e => Assert.True(e.TransactionWasActive));
    }

    [PgFact]
    public async Task Repackaging_a_rigged_ship_requires_confirmation_then_offloads_contents_and_voids_insurance()
    {
        var hook = new RecordingShipInsuranceInvalidator();
        await using var rig = await Rig.CreateAsync(hook);
        var character = await rig.NewCharacterAsync();
        var shipId = await rig.GiveAsync(character.CharacterId, Rig.Cockroach);
        var rigType = (await rig.Statics.TypesInCategoryAsync("Module"))
            .First(type => Eve.Fitting.Slots.FamilyOfType(type) == Eve.Fitting.SlotFamily.Rig);
        var rigId = await rig.GiveAsync(
            character.CharacterId,
            rigType.TypeId,
            location: shipId,
            flag: Eve.Fitting.Slots.FirstFlag(Eve.Fitting.SlotFamily.Rig));
        var moduleId = await rig.GiveAsync(
            character.CharacterId,
            Rig.Afterburner,
            location: shipId,
            flag: ItemFlags.HiSlot0);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Modules.SetAsync(connection, transaction, moduleId, ItemState.Online);
            await transaction.CommitAsync();
        }

        var request = new RepackageItemRequest(
            shipId, Rig.Station, character.CharacterId, Rig.Station, ItemFlags.Hangar);
        var (unchanged, confirmation) = await rig.Ships.RepackageItemsAsync(
            character.CharacterId, [request], destroyable: false);
        Assert.Empty(unchanged);
        Assert.Equal([shipId], confirmation);
        Assert.True((await rig.Items.GetAsync(shipId))!.Singleton);
        Assert.Empty(hook.Events);

        var (changes, secondConfirmation) = await rig.Ships.RepackageItemsAsync(
            character.CharacterId, [request], destroyable: true);
        var changed = Assert.Single(changes);
        Assert.Empty(secondConfirmation);
        Assert.False((await rig.Items.GetAsync(shipId))!.Singleton);
        Assert.Null(await rig.Items.GetAsync(rigId));
        var returnedModule = await rig.Items.GetAsync(moduleId);
        Assert.Equal(Rig.Station, returnedModule!.LocationId);
        Assert.Equal(ItemFlags.Hangar, returnedModule.Flag);
        Assert.Equal(ItemState.Offline, (await rig.Modules.StatesAsync([moduleId]))[moduleId]);
        Assert.Contains(changed.Events, effect => effect is EffectChanged);
        var invalidation = Assert.Single(hook.Events);
        Assert.Equal(shipId, invalidation.ItemId);
        Assert.Equal(ShipContractInvalidationReason.Repackaged, invalidation.Reason);
        Assert.True(invalidation.TransactionWasActive);
    }


    [PgFact]
    public async Task A_specialized_fuel_hold_uses_the_current_SDE_capacity_and_item_rules()
    {
        await using var rig = await Rig.CreateAsync();
        var fuelCapacityId = rig.Statics.AttributeId("specialFuelBayCapacity");
        Assert.NotNull(fuelCapacityId);
        var carrier = (await rig.Statics.TypesInCategoryAsync("Ship"))
            .First(type => type.Attributes.GetValueOrDefault(fuelCapacityId.Value) > 0);
        var character = await rig.NewCharacterAsync();
        var inactiveCarrier = await rig.GiveAsync(character.CharacterId, carrier.TypeId);
        var inactiveFuelRoom = await rig.Ships.RoomAsync(character.CharacterId, inactiveCarrier, 133);
        Assert.True(inactiveFuelRoom is { Capacity: > 0 });

        var heavyWater = (await rig.Statics.TypesInCategoryAsync("Material"))
            .First(type => string.Equals(type.Name, "Heavy Water", StringComparison.OrdinalIgnoreCase));
        var fuel = await rig.GiveAsync(character.CharacterId, heavyWater.TypeId, singleton: false, quantity: 10);
        var moved = Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(
            character.CharacterId, fuel, Rig.Station, inactiveCarrier, 133, null));
        Assert.Equal(133, Assert.Single(moved.Stacks!).After.Flag);

        var module = await rig.GiveAsync(character.CharacterId, Rig.Afterburner);
        var refused = Assert.IsType<ShipRefused>(await rig.Ships.MoveAsync(
            character.CharacterId, module, Rig.Station, inactiveCarrier, 133, null));
        Assert.Equal(ShipRefusal.NotAllowedInHold, refused.Reason);
    }

    [PgFact]
    public async Task A_packaged_inactive_ship_assembles_when_a_specialized_bay_receives_an_item()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync(shipTypeId: Rig.Cockroach);
        var packagedOrca = await rig.GiveAsync(character.CharacterId, 28606, singleton: false);
        var heavyWater = await rig.GiveAsync(character.CharacterId, 16272, singleton: false, quantity: 10);

        var changed = Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(
            character.CharacterId, heavyWater, Rig.Station, packagedOrca, 133, null));

        Assert.Equal(packagedOrca, changed.ShipId);
        Assert.True((await rig.Items.GetAsync(packagedOrca))!.Singleton);
        Assert.Contains(changed.Stacks!, item => item.After.Id == packagedOrca && item.After.Singleton);
        Assert.Contains(changed.Stacks!, item => item.After.Id == heavyWater && item.After.Flag == 133);
        Assert.True((await rig.Ships.RoomAsync(character.CharacterId, packagedOrca, 133)) is { Capacity: > 0, Used: > 0 });
    }


    [PgFact]
    public async Task Frigate_escape_bay_checks_current_SDE_capacity_hull_class_skills_cargo_and_contents()
    {
        await using var rig = await Rig.CreateAsync();
        var escapeCapacityId = rig.Statics.AttributeId("frigateEscapeBayCapacity");
        Assert.NotNull(escapeCapacityId);
        var battleship = (await rig.Statics.TypesInCategoryAsync("Ship"))
            .First(type => type.Attributes.GetValueOrDefault(escapeCapacityId.Value) > 0);
        var allowedFrigate = (await rig.Statics.TypesInGroupsAsync(["Frigate", "Assault Frigate", "Electronic Attack Ship", "Logistics Frigate"]))
            .First(type => type.CategoryId == 6);
        var interceptor = (await rig.Statics.TypesInGroupsAsync(["Interceptor"])).First(type => type.CategoryId == 6);
        var asteroidOre = (await rig.Statics.TypesInGroupsAsync(["Asteroid Ore"])).First();
        var character = await rig.NewCharacterAsync(shipTypeId: battleship.TypeId);
        foreach (var skillId in allowedFrigate.RequiredSkills)
            await rig.SetSkillAsync(character.CharacterId, skillId, 5);

        var emptyRoom = await rig.Ships.RoomAsync(character.CharacterId, character.ShipId, CharacterShips.FrigateEscapeBayFlag);
        Assert.True(emptyRoom is { Capacity: >= 1, Used: 0 });

        var wrongClass = await rig.GiveAsync(character.CharacterId, interceptor.TypeId, singleton: true);
        var wrongClassResult = Assert.IsType<ShipRefused>(await rig.Ships.MoveAsync(
            character.CharacterId, wrongClass, Rig.Station, character.ShipId, CharacterShips.FrigateEscapeBayFlag, null));
        Assert.Equal(ShipRefusal.NotAllowedInHold, wrongClassResult.Reason);

        var cargoRestricted = await rig.GiveAsync(character.CharacterId, allowedFrigate.TypeId, singleton: true);
        await rig.GiveAsync(character.CharacterId, asteroidOre.TypeId, singleton: false, quantity: 1,
            location: cargoRestricted, flag: CharacterShips.CargoFlag);
        var cargoResult = Assert.IsType<ShipRefused>(await rig.Ships.MoveAsync(
            character.CharacterId, cargoRestricted, Rig.Station, character.ShipId, CharacterShips.FrigateEscapeBayFlag, null));
        Assert.Equal(ShipRefusal.NotAllowedInHold, cargoResult.Reason);

        var validFrigate = await rig.GiveAsync(character.CharacterId, allowedFrigate.TypeId, singleton: true);
        await rig.GiveAsync(character.CharacterId, Rig.Afterburner, singleton: true,
            location: validFrigate, flag: CharacterShips.CargoFlag);
        Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(
            character.CharacterId, validFrigate, Rig.Station, character.ShipId, CharacterShips.FrigateEscapeBayFlag, null));
        var fullRoom = await rig.Ships.RoomAsync(character.CharacterId, character.ShipId, CharacterShips.FrigateEscapeBayFlag);
        Assert.Equal(1, fullRoom?.Used);

        var secondFrigate = await rig.GiveAsync(character.CharacterId, allowedFrigate.TypeId, singleton: true);
        var fullResult = Assert.IsType<ShipRefused>(await rig.Ships.MoveAsync(
            character.CharacterId, secondFrigate, Rig.Station, character.ShipId, CharacterShips.FrigateEscapeBayFlag, null));
        Assert.Equal(ShipRefusal.NotEnoughCargoSpace, fullResult.Reason);

        var packagedFrigate = await rig.GiveAsync(character.CharacterId, allowedFrigate.TypeId, singleton: false);
        var packagedResult = Assert.IsType<ShipRefused>(await rig.Ships.MoveAsync(
            character.CharacterId, packagedFrigate, Rig.Station, character.ShipId, CharacterShips.FrigateEscapeBayFlag, null));
        Assert.Equal(ShipRefusal.NotAllowedInHold, packagedResult.Reason);
    }


    [PgFact]
    public async Task Ship_maintenance_bay_and_fleet_hangar_use_SDE_capacity_and_reject_invalid_assembled_ship_contents()
    {
        await using var rig = await Rig.CreateAsync();
        var maintenanceAttribute = rig.Statics.AttributeId("shipMaintenanceBayCapacity");
        var fleetAttribute = rig.Statics.AttributeId("fleetHangarCapacity");
        Assert.NotNull(maintenanceAttribute);
        Assert.NotNull(fleetAttribute);
        var carrier = (await rig.Statics.TypesInCategoryAsync("Ship")).First(type =>
            type.Attributes.GetValueOrDefault(maintenanceAttribute.Value) > 0
            && type.Attributes.GetValueOrDefault(fleetAttribute.Value) > 0);
        var character = await rig.NewCharacterAsync();
        var carrierId = await rig.GiveAsync(character.CharacterId, carrier.TypeId, singleton: true);

        var maintenanceRoom = await rig.Ships.RoomAsync(character.CharacterId, carrierId, CharacterShips.ShipMaintenanceBayFlag);
        var fleetRoom = await rig.Ships.RoomAsync(character.CharacterId, carrierId, CharacterShips.FleetHangarFlag);
        Assert.True(maintenanceRoom is { Capacity: > 0, Used: 0 });
        Assert.True(fleetRoom is { Capacity: > 0 });

        var activeHull = (await rig.Items.GetAsync(character.ShipId))!;
        var assembledShip = await rig.GiveAsync(character.CharacterId, activeHull.TypeId, singleton: true);
        Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(
            character.CharacterId, assembledShip, Rig.Station, carrierId, CharacterShips.ShipMaintenanceBayFlag, null));

        var packagedShip = await rig.GiveAsync(character.CharacterId, activeHull.TypeId, singleton: false);
        var packagedResult = Assert.IsType<ShipRefused>(await rig.Ships.MoveAsync(
            character.CharacterId, packagedShip, Rig.Station, carrierId, CharacterShips.ShipMaintenanceBayFlag, null));
        Assert.Equal(ShipRefusal.NotAllowedInHold, packagedResult.Reason);

        var ore = (await rig.Statics.TypesInGroupsAsync(["Asteroid Ore"])).First();
        var contaminatedShip = await rig.GiveAsync(character.CharacterId, activeHull.TypeId, singleton: true);
        await rig.GiveAsync(character.CharacterId, ore.TypeId, singleton: false, quantity: 1,
            location: contaminatedShip, flag: CharacterShips.CargoFlag);
        var cargoResult = Assert.IsType<ShipRefused>(await rig.Ships.MoveAsync(
            character.CharacterId, contaminatedShip, Rig.Station, carrierId, CharacterShips.ShipMaintenanceBayFlag, null));
        Assert.Equal(ShipRefusal.NotAllowedInHold, cargoResult.Reason);

        var module = await rig.GiveAsync(character.CharacterId, Rig.Afterburner, singleton: true);
        Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(
            character.CharacterId, module, Rig.Station, carrierId, CharacterShips.FleetHangarFlag, null));
        Assert.True((await rig.Ships.RoomAsync(character.CharacterId, carrierId, CharacterShips.FleetHangarFlag))?.Used > 0);
    }


    [PgFact]
    public async Task Fuel_mobile_depot_booster_and_subsystem_holds_follow_current_SDE_groups()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var shipTypes = await rig.Statics.TypesInCategoryAsync("Ship");
        async Task<(int Flag, long ShipId)> HoldAsync(string attributeName, int flag)
        {
            var attribute = rig.Statics.AttributeId(attributeName);
            Assert.NotNull(attribute);
            var hull = shipTypes.First(type => type.Attributes.GetValueOrDefault(attribute.Value) > 0);
            var ship = await rig.GiveAsync(character.CharacterId, hull.TypeId, singleton: true);
            Assert.True((await rig.Ships.RoomAsync(character.CharacterId, ship, flag)) is { Capacity: > 0, Used: 0 });
            return (flag, ship);
        }

        var (fuelFlag, fuelShip) = await HoldAsync("specialFuelBayCapacity", 133);
        var fuelBlock = (await rig.Statics.TypesInGroupsAsync(["Fuel Block"])).First();
        var fuelBlockItem = await rig.GiveAsync(character.CharacterId, fuelBlock.TypeId, singleton: false);
        Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(character.CharacterId, fuelBlockItem, Rig.Station, fuelShip, fuelFlag, null));

        var (mobileFlag, mobileShip) = await HoldAsync("specialMobileDepotHoldCapacity", 176);
        var depot = (await rig.Statics.TypesInGroupsAsync(["Mobile Depot"])).First();
        var depotItem = await rig.GiveAsync(character.CharacterId, depot.TypeId, singleton: true);
        Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(character.CharacterId, depotItem, Rig.Station, mobileShip, mobileFlag, null));
        var wrongMobileItem = await rig.GiveAsync(character.CharacterId, fuelBlock.TypeId);
        Assert.Equal(ShipRefusal.NotAllowedInHold, Assert.IsType<ShipRefused>(await rig.Ships.MoveAsync(
            character.CharacterId, wrongMobileItem, Rig.Station, mobileShip, mobileFlag, null)).Reason);

        var (boosterFlag, boosterShip) = await HoldAsync("specialBoosterHoldCapacity", 177);
        var booster = (await rig.Statics.TypesInGroupsAsync(["Booster"])).First();
        var boosterItem = await rig.GiveAsync(character.CharacterId, booster.TypeId, singleton: true);
        Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(character.CharacterId, boosterItem, Rig.Station, boosterShip, boosterFlag, null));

        var (subsystemFlag, subsystemShip) = await HoldAsync("specialSubsystemHoldCapacity", 183);
        var subsystem = (await rig.Statics.TypesInCategoryAsync("Subsystem")).First();
        var subsystemItem = await rig.GiveAsync(character.CharacterId, subsystem.TypeId, singleton: true);
        Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(character.CharacterId, subsystemItem, Rig.Station, subsystemShip, subsystemFlag, null));
        var wrongSubsystemItem = await rig.GiveAsync(character.CharacterId, booster.TypeId, singleton: true);
        Assert.Equal(ShipRefusal.NotAllowedInHold, Assert.IsType<ShipRefused>(await rig.Ships.MoveAsync(
            character.CharacterId, wrongSubsystemItem, Rig.Station, subsystemShip, subsystemFlag, null)).Reason);
    }

    [PgFact]
    public async Task Every_restricted_specialized_hold_accepts_a_current_SDE_sample_and_refuses_a_module()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var shipTypes = await rig.Statics.TypesInCategoryAsync("Ship");
        async Task<TypeDef> CategorySample(string name) =>
            (await rig.Statics.TypesInCategoryAsync(name)).First();
        async Task<TypeDef> GroupSample(params string[] names) =>
            (await rig.Statics.TypesInGroupsAsync(names)).First();

        var drone = await CategorySample("Drone");
        var fighter = await CategorySample("Fighter");
        var fuelBlock = await GroupSample("Fuel Block");
        var asteroidOre = await GroupSample("Asteroid Ore");
        var prismaticite = await GroupSample("Prismaticite");
        var ice = await GroupSample("Ice");
        var gas = await GroupSample("Compressed Gas");
        var mineral = await GroupSample("Mineral");
        var salvage = await GroupSample("Salvaged Materials");
        var frigate = await GroupSample("Frigate");
        var cruiser = await GroupSample("Cruiser");
        var battleship = await GroupSample("Battleship");
        var hauler = await GroupSample("Hauler");
        var nanitePaste = await GroupSample("Nanite Repair Paste");
        var commandCenter = await GroupSample("Command Centers");
        var planetaryCommodity = await CategorySample("Planetary Commodities");
        var material = (await rig.Statics.TypesInCategoryAsync("Material"))
            .First(type => type.Name == "Tritanium");
        var quafe = (await rig.Statics.TypesInGroupsAsync(
                ["General", "Booster", "Miscellaneous", "Commodities", "Special Edition Commodities"]))
            .First(type => type.Name == "Quafe");
        var corpse = (await rig.Statics.TypesInGroupsAsync(["Biomass"]))
            .First(type => type.Name == "Corpse Male");
        var mobileDepot = await GroupSample("Mobile Depot");
        var booster = await GroupSample("Booster");
        var subsystem = await CategorySample("Subsystem");
        var colonyResource = await GroupSample("Colony Reagents");
        var moonMaterial = await GroupSample("Moon Materials");
        var expeditionItem = mobileDepot;

        var samples = new[]
        {
            (CharacterShips.DroneBayFlag, "droneCapacity", drone, false),
            (CharacterShips.FighterBayFlag, "fighterCapacity", fighter, false),
            (133, "specialFuelBayCapacity", fuelBlock, false),
            (134, "generalMiningHoldCapacity", asteroidOre, false),
            (182, "specialAsteroidHoldCapacity", prismaticite, false),
            (181, "specialIceHoldCapacity", ice, false),
            (135, "specialGasHoldCapacity", gas, false),
            (136, "specialMineralHoldCapacity", mineral, false),
            (137, "specialSalvageHoldCapacity", salvage, false),
            (138, "specialShipHoldCapacity", frigate, false),
            (139, "specialSmallShipHoldCapacity", frigate, false),
            (140, "specialMediumShipHoldCapacity", cruiser, false),
            (141, "specialLargeShipHoldCapacity", battleship, false),
            (142, "specialIndustrialShipHoldCapacity", hauler, false),
            (143, "specialAmmoHoldCapacity", nanitePaste, false),
            (148, "specialCommandCenterHoldCapacity", commandCenter, false),
            (149, "specialPlanetaryCommoditiesHoldCapacity", planetaryCommodity, false),
            (151, "specialMaterialBayCapacity", material, false),
            (154, "specialQuafeHoldCapacity", quafe, false),
            (174, "specialCorpseHoldCapacity", corpse, true),
            (176, "specialMobileDepotHoldCapacity", mobileDepot, false),
            (177, "specialBoosterHoldCapacity", booster, false),
            (183, "specialSubsystemHoldCapacity", subsystem, false),
            (185, "specialColonyResourcesHoldCapacity", colonyResource, false),
            (186, "outputMoonMaterialBayCapacity", moonMaterial, false),
            (188, "specialExpeditionHoldCapacity", expeditionItem, false),
        };

        var invalidModule = await rig.GiveAsync(character.CharacterId, Rig.Afterburner, singleton: true);
        foreach (var (flag, attributeName, sample, singleton) in samples)
        {
            var capacityAttribute = rig.Statics.AttributeId(attributeName);
            Assert.NotNull(capacityAttribute);
            var hull = shipTypes.First(type => type.Attributes.GetValueOrDefault(capacityAttribute.Value) > 0);
            var hullId = await rig.GiveAsync(character.CharacterId, hull.TypeId, singleton: true);
            var itemId = await rig.GiveAsync(character.CharacterId, sample.TypeId, singleton);
            var moved = await rig.Ships.MoveAsync(character.CharacterId, itemId, Rig.Station, hullId, flag, null);
            Assert.IsType<ShipChanged>(moved);
            Assert.Contains(await rig.Items.InContainerAsync(hullId), item => item.Id == itemId && item.Flag == flag);

            var refused = Assert.IsType<ShipRefused>(await rig.Ships.MoveAsync(
                character.CharacterId, invalidModule, Rig.Station, hullId, flag, null));
            Assert.Equal(ShipRefusal.NotAllowedInHold, refused.Reason);
        }
    }

    [PgFact]
    public async Task Every_ship_bay_capacity_comes_from_current_SDE_dogma_attributes()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var shipTypes = await rig.Statics.TypesInCategoryAsync("Ship");
        var bays = new (int Flag, string AttributeName)[]
        {
            (CharacterShips.CargoFlag, "capacity"),
            (CharacterShips.DroneBayFlag, "droneCapacity"),
            (CharacterShips.FighterBayFlag, "fighterCapacity"),
            (CharacterShips.FrigateEscapeBayFlag, "frigateEscapeBayCapacity"),
            (CharacterShips.ShipMaintenanceBayFlag, "shipMaintenanceBayCapacity"),
            (CharacterShips.FleetHangarFlag, "fleetHangarCapacity"),
            (133, "specialFuelBayCapacity"),
            (134, "generalMiningHoldCapacity"),
            (182, "specialAsteroidHoldCapacity"),
            (181, "specialIceHoldCapacity"),
            (135, "specialGasHoldCapacity"),
            (136, "specialMineralHoldCapacity"),
            (137, "specialSalvageHoldCapacity"),
            (138, "specialShipHoldCapacity"),
            (139, "specialSmallShipHoldCapacity"),
            (140, "specialMediumShipHoldCapacity"),
            (141, "specialLargeShipHoldCapacity"),
            (142, "specialIndustrialShipHoldCapacity"),
            (143, "specialAmmoHoldCapacity"),
            (148, "specialCommandCenterHoldCapacity"),
            (149, "specialPlanetaryCommoditiesHoldCapacity"),
            (151, "specialMaterialBayCapacity"),
            (154, "specialQuafeHoldCapacity"),
            (174, "specialCorpseHoldCapacity"),
            (176, "specialMobileDepotHoldCapacity"),
            (177, "specialBoosterHoldCapacity"),
            (183, "specialSubsystemHoldCapacity"),
            (185, "specialColonyResourcesHoldCapacity"),
            (186, "outputMoonMaterialBayCapacity"),
            (188, "specialExpeditionHoldCapacity"),
        };

        foreach (var (flag, attributeName) in bays)
        {
            var attribute = rig.Statics.AttributeId(attributeName);
            Assert.True(attribute is not null, $"SDE attribute {attributeName} is missing");
            var hullType = shipTypes.FirstOrDefault(type => type.Attributes.GetValueOrDefault(attribute!.Value) > 0);
            Assert.True(hullType is not null, $"no current SDE ship has capacity for bay flag {flag} ({attributeName})");
            var hullId = await rig.GiveAsync(character.CharacterId, hullType!.TypeId, singleton: true);
            var room = await rig.Ships.RoomAsync(character.CharacterId, hullId, flag);
            Assert.NotNull(room);
            Assert.Equal(hullType.Attributes[attribute!.Value], room!.Value.Capacity);
            Assert.Equal(0, room.Value.Used);
        }
    }


    [PgFact]
    public async Task Docked_owned_containers_move_items_in_both_directions_and_keep_capacity()
    {
        await using var rig = await Rig.CreateAsync();
        var capacityId = rig.Statics.AttributeId("capacity");
        Assert.NotNull(capacityId);
        var containerType = (await rig.Statics.TypesInGroupsAsync([
            "Cargo Container", "Secure Cargo Container", "Freight Container", "Audit Log Secure Container"
        ])).First(type => type.Attributes.GetValueOrDefault(capacityId.Value) > 0);
        var character = await rig.NewCharacterAsync();
        var container = await rig.GiveAsync(character.CharacterId, containerType.TypeId, singleton: true);
        var item = await rig.GiveAsync(character.CharacterId, Rig.Afterburner, singleton: true);

        var added = Assert.IsType<ShipChanged>(await rig.Ships.MoveContainerAsync(
            character.CharacterId, item, Rig.Station, container, null));
        Assert.Equal(container, Assert.Single(added.Stacks!).After.LocationId);
        Assert.Equal(CharacterShips.ContainerFlag, added.Stacks!.Single().After.Flag);
        var room = await rig.Ships.ContainerRoomAsync(container);
        Assert.True(room is { Capacity: > 0, Used: > 0 });

        var removed = Assert.IsType<ShipChanged>(await rig.Ships.MoveContainerAsync(
            character.CharacterId, item, container, Rig.Station, null));
        Assert.Equal(Rig.Station, Assert.Single(removed.Stacks!).After.LocationId);
        Assert.Equal(ItemFlags.Hangar, removed.Stacks!.Single().After.Flag);
    }


    [PgFact]
    public async Task Docked_container_moves_reject_foreign_items_and_container_cycles()
    {
        await using var rig = await Rig.CreateAsync();
        var capacityId = rig.Statics.AttributeId("capacity");
        Assert.NotNull(capacityId);
        var containerType = (await rig.Statics.TypesInGroupsAsync([
            "Cargo Container", "Secure Cargo Container", "Freight Container", "Audit Log Secure Container"
        ])).First(type => type.Attributes.GetValueOrDefault(capacityId.Value) > 0);
        var character = await rig.NewCharacterAsync();
        var parent = await rig.GiveAsync(character.CharacterId, containerType.TypeId, singleton: true);
        var nested = await rig.GiveAsync(character.CharacterId, containerType.TypeId, singleton: true);
        var foreignItem = await rig.GiveAsync(character.CharacterId + 1, Rig.Afterburner, singleton: true);

        var nestedMove = Assert.IsType<ShipChanged>(await rig.Ships.MoveContainerAsync(
            character.CharacterId, nested, Rig.Station, parent, null));
        Assert.Equal(parent, Assert.Single(nestedMove.Stacks!).After.LocationId);

        Assert.IsType<NothingHappened>(await rig.Ships.MoveContainerAsync(
            character.CharacterId, parent, Rig.Station, nested, null));
        Assert.IsType<NothingHappened>(await rig.Ships.MoveContainerAsync(
            character.CharacterId, foreignItem, Rig.Station, parent, null));
    }

    private sealed class RecordingShipInsuranceInvalidator : IShipInsuranceInvalidator
    {
        public List<(long ItemId, ShipContractInvalidationReason Reason, bool TransactionWasActive)> Events { get; } = [];

        public ValueTask InvalidateAsync(
            Npgsql.NpgsqlConnection connection,
            Npgsql.NpgsqlTransaction transaction,
            long itemId,
            ShipContractInvalidationReason reason,
            CancellationToken cancellationToken)
        {
            Events.Add((itemId, reason, ReferenceEquals(connection, transaction.Connection)));
            return ValueTask.CompletedTask;
        }
    }
}

