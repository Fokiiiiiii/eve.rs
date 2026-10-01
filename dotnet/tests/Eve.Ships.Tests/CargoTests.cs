using Eve.Characters;
using Eve.Inventory;

namespace Eve.Ships.Tests;

/// <summary>The cargo hold of a docked ship: what goes in whole or in part, what does not fit, the stacks joined, and the rooms (the rules of the reference's recording).</summary>
public class CargoTests
{
    private const int Tritanium = 34, Pyerite = 35;

    // the ibis holds 125 cubic metres; a unit of tritanium is 0.01
    private const double IbisHold = 125;

    private static async Task<long> StackAsync(Rig rig, Created c, int type, long quantity) => await rig.GiveAsync(c.CharacterId, type, singleton: false, quantity: quantity);

    private static Task<ShipOutcome> IntoHoldAsync(Rig rig, Created c, long stack, long? quantity) =>
        rig.Ships.MoveAsync(c.CharacterId, stack, Rig.Station, c.ShipId, CharacterShips.CargoFlag, quantity);

    [PgFact]
    public async Task A_whole_stack_goes_into_the_hold_as_it_is_and_the_client_is_told_where_it_came_from()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var stack = await StackAsync(rig, c, Tritanium, 100);

        var changed = Assert.IsType<ShipChanged>(await IntoHoldAsync(rig, c, stack, 100));

        Assert.Equal(stack, changed.ItemId);
        Assert.Empty(changed.Items);
        var told = Assert.Single(changed.Stacks!);
        Assert.Equal((stack, c.ShipId, CharacterShips.CargoFlag, 100L), (told.After.Id, told.After.LocationId, told.After.Flag, told.After.Quantity));
        Assert.Equal((Rig.Station, ItemFlags.Hangar, 100L), (told.PreviousLocationId, told.PreviousFlag, told.PreviousQuantity));
        Assert.Equal(c.ShipId, (await rig.Items.GetAsync(stack))!.LocationId);
    }

    [PgFact]
    public async Task Part_of_a_stack_is_split_off_into_a_stack_of_its_own_and_the_client_is_told_the_one_that_gave_and_the_one_that_took()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var stack = await StackAsync(rig, c, Tritanium, 40);

        var changed = Assert.IsType<ShipChanged>(await IntoHoldAsync(rig, c, stack, 15));

        Assert.NotEqual(stack, changed.ItemId);
        Assert.Equal(2, changed.Stacks!.Count);
        var (gave, took) = (changed.Stacks[0], changed.Stacks[1]);
        // the one that gave: the same stack with 25 left, and it was 40
        Assert.Equal((stack, 25L, 40L), (gave.After.Id, gave.After.Quantity, gave.PreviousQuantity));
        Assert.Equal((Rig.Station, ItemFlags.Hangar), (gave.After.LocationId, gave.After.Flag));
        // the one that took: new, 15 in the hold, and nothing before
        Assert.Equal((changed.ItemId, c.ShipId, CharacterShips.CargoFlag, 15L), (took.After.Id, took.After.LocationId, took.After.Flag, took.After.Quantity));
        Assert.Equal((0L, CharacterShips.CargoFlag, 0L), (took.PreviousLocationId, took.PreviousFlag, took.PreviousQuantity));
        Assert.Equal(25, (await rig.Items.GetAsync(stack))!.Quantity);
    }

    [PgFact]
    public async Task What_does_not_fit_is_refused_with_the_room_that_is_left_and_the_room_it_needs_and_nothing_moves()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var held = await StackAsync(rig, c, Tritanium, 115);
        Assert.IsType<ShipChanged>(await IntoHoldAsync(rig, c, held, null));
        var big = await StackAsync(rig, c, Pyerite, 20000);

        var refused = Assert.IsType<ShipRefused>(await IntoHoldAsync(rig, c, big, 20000));

        // 125 - 115 x 0.01 is left; 20000 pyerite (0.01 each) need 200
        Assert.Equal(ShipRefusal.NotEnoughCargoSpace, refused.Reason);
        Assert.Equal((123.85, 200d), (refused.Available, refused.Volume));
        Assert.Equal(Rig.Station, (await rig.Items.GetAsync(big))!.LocationId);
        var room = Assert.NotNull(await rig.Ships.RoomAsync(c.CharacterId, c.ShipId, CharacterShips.CargoFlag));
        Assert.Equal((IbisHold, 1.15), (room.Capacity, Math.Round(room.Used, 9)));
    }

    [PgFact]
    public async Task A_stack_that_fits_in_part_is_not_refused_for_the_part_that_does_not_go()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var big = await StackAsync(rig, c, Pyerite, 20000);

        // 1000 units take 10 cubic metres of 125: the rest of the stack is not asked about
        Assert.IsType<ShipChanged>(await IntoHoldAsync(rig, c, big, 1000));
        Assert.Equal(19000, (await rig.Items.GetAsync(big))!.Quantity);
    }

    [PgFact]
    public async Task More_than_the_stack_holds_or_less_than_one_is_nothing()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var stack = await StackAsync(rig, c, Tritanium, 10);

        Assert.IsType<NothingHappened>(await IntoHoldAsync(rig, c, stack, 11));
        Assert.IsType<NothingHappened>(await IntoHoldAsync(rig, c, stack, 0));
        Assert.Equal((Rig.Station, 10L), ((await rig.Items.GetAsync(stack))!.LocationId, (await rig.Items.GetAsync(stack))!.Quantity));
    }

    [PgFact]
    public async Task Some_of_what_is_in_the_hold_goes_back_to_the_hangar_as_a_stack_of_its_own()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var stack = await StackAsync(rig, c, Tritanium, 115);
        await IntoHoldAsync(rig, c, stack, null);

        var changed = Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(c.CharacterId, stack, c.ShipId, Rig.Station, ItemFlags.Hangar, 20));

        var took = changed.Stacks![1].After;
        Assert.Equal((Rig.Station, ItemFlags.Hangar, 20L), (took.LocationId, took.Flag, took.Quantity));
        Assert.Equal(95, (await rig.Items.GetAsync(stack))!.Quantity);
        Assert.Equal(CharacterShips.CargoFlag, (await rig.Items.GetAsync(stack))!.Flag);
    }

    [PgFact]
    public async Task StackAll_joins_the_stacks_of_a_type_into_the_oldest_and_tells_the_one_that_grew_then_the_one_that_went()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var first = await StackAsync(rig, c, Tritanium, 100);
        await IntoHoldAsync(rig, c, first, null);
        var second = await StackAsync(rig, c, Tritanium, 40);
        var split = Assert.IsType<ShipChanged>(await IntoHoldAsync(rig, c, second, 15));

        var changes = await rig.Ships.StackAllAsync(c.CharacterId, c.ShipId, CharacterShips.CargoFlag);

        Assert.Equal(2, changes.Count);
        Assert.Equal((first, 115L, 100L), (changes[0].After.Id, changes[0].After.Quantity, changes[0].PreviousQuantity));
        Assert.Equal((split.ItemId, CharacterShips.JunkLocation, c.ShipId, 15L), (changes[1].After.Id, changes[1].After.LocationId, changes[1].PreviousLocationId, changes[1].After.Quantity));
        Assert.Null(await rig.Items.GetAsync(split.ItemId));
        Assert.Equal(115, (await rig.Items.GetAsync(first))!.Quantity);
        // nothing is left to stack
        Assert.Empty(await rig.Ships.StackAllAsync(c.CharacterId, c.ShipId, CharacterShips.CargoFlag));
    }

    [PgFact]
    public async Task StackAll_leaves_other_types_other_flags_and_assembled_items_alone()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var trit = await StackAsync(rig, c, Tritanium, 10);
        var pye = await StackAsync(rig, c, Pyerite, 10);
        await IntoHoldAsync(rig, c, trit, null);
        await IntoHoldAsync(rig, c, pye, null);
        var inHangar = await StackAsync(rig, c, Tritanium, 5); // the hangar is another place

        Assert.Empty(await rig.Ships.StackAllAsync(c.CharacterId, c.ShipId, CharacterShips.CargoFlag));
        Assert.Empty(await rig.Ships.StackAllAsync(c.CharacterId, Rig.Station, ItemFlags.Hangar)); // the ship and the stack are not two of a kind
        Assert.Equal(5, (await rig.Items.GetAsync(inHangar))!.Quantity);
    }

    [PgFact]
    public async Task A_hold_is_as_big_as_the_ship_says_the_drone_bay_has_its_own_size_and_a_flag_with_no_bay_holds_nothing()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();

        var cargo = Assert.NotNull(await rig.Ships.RoomAsync(c.CharacterId, c.ShipId, CharacterShips.CargoFlag));
        var drones = Assert.NotNull(await rig.Ships.RoomAsync(c.CharacterId, c.ShipId, CharacterShips.DroneBayFlag));
        var nothing = Assert.NotNull(await rig.Ships.RoomAsync(c.CharacterId, c.ShipId, 99));

        Assert.Equal((IbisHold, 0d), (cargo.Capacity, cargo.Used));
        Assert.Equal(5d, drones.Capacity); // the ibis has a drone bay of five cubic metres
        Assert.Equal(0d, nothing.Capacity);
        Assert.Null(await rig.Ships.RoomAsync(c.CharacterId + 1, c.ShipId, CharacterShips.CargoFlag)); // not the character's ship
    }
}

/// <summary>A sale and a move of the same stack at the same moment (the row-lock convention of the market, ADR 0003): every unit is somewhere once.</summary>
public class CargoRaceTests
{
    private const int Tritanium = 34;
    private const long Escrow = 9_200_000_001;

    [PgFact]
    public async Task A_move_that_read_a_stack_a_sale_then_took_from_moves_what_is_left()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var stack = (await rig.Items.GetAsync(await rig.GiveAsync(c.CharacterId, Tritanium, singleton: false, quantity: 100)))!;
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await rig.Items.LockAndGetAsync(connection, transaction, stack.Id);

        var move = rig.Ships.MoveAsync(c.CharacterId, stack.Id, Rig.Station, c.ShipId, CharacterShips.CargoFlag, null);
        await Task.Delay(700);
        Assert.False(move.IsCompleted, "the move did not wait for the row the sale holds");
        await rig.Items.MoveStackAsync(connection, transaction, stack, 60, stack.OwnerId, Escrow, ItemFlags.Hangar);
        await transaction.CommitAsync();

        var changed = Assert.IsType<ShipChanged>(await move);
        // the sale took 60: what the move takes into the hold is the 40 that were left, and every one of the 100 is somewhere once
        var inHold = Assert.Single(await rig.Items.InLocationAsync(c.ShipId, c.CharacterId), i => i.TypeId == Tritanium);
        var escrowed = Assert.Single(await rig.Items.InLocationAsync(Escrow, c.CharacterId));
        Assert.Equal((40L, 60L), (inHold.Quantity, escrowed.Quantity));
        Assert.Equal(inHold.Id, changed.ItemId);
    }

    [PgFact]
    public async Task A_stack_a_sale_took_whole_is_not_moved_into_the_hold()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var stack = (await rig.Items.GetAsync(await rig.GiveAsync(c.CharacterId, Tritanium, singleton: false, quantity: 100)))!;
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await rig.Items.LockAndGetAsync(connection, transaction, stack.Id);

        var move = rig.Ships.MoveAsync(c.CharacterId, stack.Id, Rig.Station, c.ShipId, CharacterShips.CargoFlag, null);
        await Task.Delay(500);
        await rig.Items.MoveStackAsync(connection, transaction, stack, 100, stack.OwnerId, Escrow, ItemFlags.Hangar);
        await transaction.CommitAsync();

        Assert.IsType<NothingHappened>(await move);
        Assert.DoesNotContain(await rig.Items.InLocationAsync(c.ShipId, c.CharacterId), i => i.TypeId == Tritanium);
        Assert.Equal(100, Assert.Single(await rig.Items.InLocationAsync(Escrow, c.CharacterId)).Quantity);
    }

    [PgFact]
    public async Task Two_moves_of_the_same_stack_into_the_hold_put_it_there_once()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var stack = await rig.GiveAsync(c.CharacterId, Tritanium, singleton: false, quantity: 100);

        var moves = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => rig.Ships.MoveAsync(c.CharacterId, stack, Rig.Station, c.ShipId, CharacterShips.CargoFlag, 60)));

        // the first takes 60; the others find 40 left (and a source that is no longer where they were told it is)
        var inHold = (await rig.Items.InLocationAsync(c.ShipId, c.CharacterId)).Where(i => i.TypeId == Tritanium).Sum(i => i.Quantity);
        var inHangar = (await rig.Items.InLocationAsync(Rig.Station, c.CharacterId)).Where(i => i.TypeId == Tritanium).Sum(i => i.Quantity);
        Assert.Equal(100, inHold + inHangar);
        Assert.Equal(moves.OfType<ShipChanged>().Count() * 60L, inHold);
    }
}
