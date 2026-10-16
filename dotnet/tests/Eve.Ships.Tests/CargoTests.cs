using Eve.Characters;
using Eve.Inventory;

namespace Eve.Ships.Tests;

/// <summary>The cargo hold of a docked ship: what goes in whole or in part, what does not fit, the stacks joined, and the rooms (the rules of the reference's recording).</summary>
public class CargoTests
{
    private const int Tritanium = 34, Pyerite = 35;

    // Tritanium and pyerite each occupy 0.01 cubic metres.

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
        var capacity = rig.Statics.Type(Rig.Cockroach)!.Attributes[38];
        const double volumePerUnit = 0.01;
        var heldQuantity = checked((long)Math.Round((capacity - volumePerUnit) / volumePerUnit));
        var held = await StackAsync(rig, c, Tritanium, heldQuantity);
        Assert.IsType<ShipChanged>(await IntoHoldAsync(rig, c, held, null));
        var big = await StackAsync(rig, c, Pyerite, 2);

        var refused = Assert.IsType<ShipRefused>(await IntoHoldAsync(rig, c, big, 2));

        Assert.Equal(ShipRefusal.NotEnoughCargoSpace, refused.Reason);
        Assert.Equal((0.01, 0.02), (Math.Round(refused.Available, 6), Math.Round(refused.Volume, 6)));
        Assert.Equal(Rig.Station, (await rig.Items.GetAsync(big))!.LocationId);
        var room = Assert.NotNull(await rig.Ships.RoomAsync(c.CharacterId, c.ShipId, CharacterShips.CargoFlag));
        Assert.Equal(capacity, room.Capacity);
        Assert.Equal(capacity - volumePerUnit, Math.Round(room.Used, 6));
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
        var type = rig.Statics.Type(Rig.Cockroach)!;

        Assert.Equal((type.Attributes[38], 0d), (cargo.Capacity, cargo.Used));
        Assert.Equal(type.Attributes[283], drones.Capacity);
        Assert.Equal(0d, nothing.Capacity);
        Assert.Null(await rig.Ships.RoomAsync(c.CharacterId + 1, c.ShipId, CharacterShips.CargoFlag));
    }
}

/// <summary>Cans and wrecks beside the ship in space: what goes between them and the hold.</summary>
public class SpaceCargoTests
{
    private const int Tritanium = 34;

    [PgFact]
    public async Task Run_space_can_provenance_is_atomic_exactly_scoped_and_preserves_custom_info()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var undocked = await rig.Ships.UndockAsync(character.CharacterId);
        var ship = undocked.Moved!.Ship;
        var systemId = checked((int)ship.LocationId);

        var firstScope = new RunSpaceItemScope(Guid.NewGuid(), 84101, 1, systemId, character.CharacterId);
        var firstCan = await rig.Ships.CreateCanAsync(character.CharacterId, systemId, runSpaceScope: firstScope);
        Assert.Equal(firstScope, firstCan.RunSpaceProvenance!.Scope);
        Assert.Equal(RunSpaceItemRole.Can, firstCan.RunSpaceProvenance.Role);
        Assert.Equal(character.CharacterId, firstCan.RunSpaceProvenance.SourceCharacterId);
        Assert.Equal(ship.Id, firstCan.RunSpaceProvenance.SourceShipId);
        Assert.Equal(ship.Id, firstCan.RunSpaceProvenance.SourceItemId);
        Assert.Equal(character.CharacterId, firstCan.OwnerId);
        Assert.Equal((long)systemId, firstCan.LocationId);
        Assert.Null(firstCan.CustomInfo);

        var secondScope = firstScope with { ContentId = Guid.NewGuid(), InstanceId = 84102 };
        var secondCan = await rig.Ships.CreateCanAsync(character.CharacterId, systemId, runSpaceScope: secondScope);
        var thirdScope = firstScope with { ContentId = Guid.NewGuid(), InstanceId = 84103 };
        var thirdProvenance = new RunSpaceItemProvenance(
            thirdScope, RunSpaceItemRole.Can, character.CharacterId, ship.Id, ship.Id);

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var legacyCustomInfoValues = new string?[]
            {
                "0.375",
                """{"schemaVersion":2,"contentId":"00000000-0000-0000-0000-000000000001","roomOrdinal":1}""",
                "evejs:structure-unanchor:hull:60015249",
                null,
            };
            foreach (var customInfo in legacyCustomInfoValues)
            {
                await rig.Items.SetCustomInfoAsync(connection, transaction, firstCan.Id, customInfo);
                var reloaded = await rig.Items.LockAndGetAsync(connection, transaction, firstCan.Id);
                Assert.NotNull(reloaded);
                Assert.Equal(customInfo, reloaded.CustomInfo);
                Assert.Equal(firstScope, reloaded.RunSpaceProvenance!.Scope);
            }

            var thirdCanId = await rig.Items.CreateAsync(
                connection,
                transaction,
                new NewItem(
                    firstCan.TypeId,
                    character.CharacterId,
                    systemId,
                    CharacterShips.InSpaceFlag,
                    1,
                    Singleton: true,
                    CustomInfo: "legacy-create-custominfo")
                {
                    RunSpaceProvenance = thirdProvenance,
                });
            await transaction.CommitAsync();

            var createdWithCustomInfo = await rig.Items.GetAsync(thirdCanId);
            Assert.NotNull(createdWithCustomInfo);
            Assert.Equal("legacy-create-custominfo", createdWithCustomInfo.CustomInfo);
            Assert.Equal(thirdProvenance, createdWithCustomInfo.RunSpaceProvenance);
        }

        var freshFirstCan = await rig.Items.GetAsync(firstCan.Id);
        Assert.NotNull(freshFirstCan);
        Assert.Null(freshFirstCan.CustomInfo);
        Assert.Equal(firstScope, freshFirstCan.RunSpaceProvenance!.Scope);

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var firstRunItems = await rig.Items.FindRunSpaceItemsAsync(connection, transaction, firstScope);
            var secondRunItems = await rig.Items.FindRunSpaceItemsAsync(connection, transaction, secondScope);
            var thirdRunItems = await rig.Items.FindRunSpaceItemsAsync(connection, transaction, thirdScope);
            var wrongRunItems = await rig.Items.FindRunSpaceItemsAsync(
                connection, transaction, firstScope with { InstanceId = 84999 });

            Assert.Single(firstRunItems);
            Assert.Equal(firstCan.Id, firstRunItems[0].Id);
            Assert.Single(secondRunItems);
            Assert.Equal(secondCan.Id, secondRunItems[0].Id);
            Assert.Single(thirdRunItems);
            Assert.Equal(RunSpaceItemRole.Can, thirdRunItems[0].RunSpaceProvenance!.Role);
            Assert.Empty(wrongRunItems);
            await transaction.CommitAsync();
        }

        var legacyCan = await rig.Ships.CreateCanAsync(character.CharacterId, systemId);
        Assert.Null(legacyCan.RunSpaceProvenance);
        Assert.Null(legacyCan.CustomInfo);
    }

    private static async Task<(Created Character, Item Can)> InSpaceWithACanAsync(Rig rig)
    {
        var c = await rig.NewCharacterAsync();
        var moved = await rig.Ships.UndockAsync(c.CharacterId);
        var system = moved.Moved!.Ship.LocationId;
        return (c, await rig.Ships.CreateCanAsync(c.CharacterId, (int)system));
    }

    private static async Task<long> InAsync(Rig rig, long owner, long place, int flag, int type, long quantity)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await rig.Items.CreateAsync(connection, transaction, new NewItem(type, owner, place, flag, quantity));
        await transaction.CommitAsync();
        return id;
    }

    [PgFact]
    public async Task What_a_pilot_takes_out_of_a_can_is_its_own_from_then_on_and_a_stack_it_does_not_take_whole_is_split()
    {
        await using var rig = await Rig.CreateAsync();
        var (c, can) = await InSpaceWithACanAsync(rig);
        var stranger = await InAsync(rig, 140_000_999, can.Id, CharacterShips.ContainerFlag, Tritanium, 50);

        var changed = Assert.IsType<ShipChanged>(await rig.Ships.MoveInSpaceAsync(c.CharacterId, stranger, can.Id, c.ShipId, 20));

        var taken = (await rig.Items.GetAsync(changed.ItemId))!;
        Assert.Equal((c.CharacterId, c.ShipId, CharacterShips.CargoFlag, 20L), (taken.OwnerId, taken.LocationId, taken.Flag, taken.Quantity));
        var left = (await rig.Items.GetAsync(stranger))!;
        Assert.Equal((140_000_999L, can.Id, 30L), (left.OwnerId, left.LocationId, left.Quantity));
        Assert.Equal(2, changed.Stacks!.Count);
    }

    [PgFact]
    public async Task A_whole_stack_taken_out_of_a_can_keeps_its_id_and_changes_hands()
    {
        await using var rig = await Rig.CreateAsync();
        var (c, can) = await InSpaceWithACanAsync(rig);
        var stranger = await InAsync(rig, 140_000_999, can.Id, CharacterShips.ContainerFlag, Tritanium, 50);

        var changed = Assert.IsType<ShipChanged>(await rig.Ships.MoveInSpaceAsync(c.CharacterId, stranger, can.Id, c.ShipId, null));

        Assert.Equal(stranger, changed.ItemId);
        var taken = (await rig.Items.GetAsync(stranger))!;
        Assert.Equal((c.CharacterId, c.ShipId, CharacterShips.CargoFlag, 50L), (taken.OwnerId, taken.LocationId, taken.Flag, taken.Quantity));
        var told = Assert.Single(changed.Stacks!);
        Assert.Equal((can.Id, CharacterShips.ContainerFlag), (told.PreviousLocationId, told.PreviousFlag));
    }

    [PgFact]
    public async Task More_than_the_hold_takes_is_refused_as_for_a_hold_and_nothing_leaves_the_can()
    {
        await using var rig = await Rig.CreateAsync();
        var (c, can) = await InSpaceWithACanAsync(rig);
        var capacity = rig.Statics.Type(Rig.Cockroach)!.Attributes[38];
        var quantity = checked((long)Math.Round(capacity / 0.01) + 1);
        var big = await InAsync(rig, c.CharacterId, can.Id, CharacterShips.ContainerFlag, Tritanium, quantity);

        var refused = Assert.IsType<ShipRefused>(await rig.Ships.MoveInSpaceAsync(c.CharacterId, big, can.Id, c.ShipId, null));

        Assert.Equal(ShipRefusal.NotEnoughCargoSpace, refused.Reason);
        Assert.Equal(capacity, refused.Available);
        Assert.Equal(Math.Round(quantity * 0.01, 6), Math.Round(refused.Volume, 6));
        Assert.Equal(can.Id, (await rig.Items.GetAsync(big))!.LocationId);
    }

    [PgFact]
    public async Task A_can_holds_what_its_type_says_and_what_does_not_fit_is_refused_as_for_a_container()
    {
        await using var rig = await Rig.CreateAsync();
        var (c, can) = await InSpaceWithACanAsync(rig);
        // the can holds 55,000 cubic metres: fill it up to 50 short, then put in a stack of 100 (one cubic metre)
        var filler = await InAsync(rig, c.CharacterId, can.Id, CharacterShips.ContainerFlag, Tritanium, 5_499_950);
        var hold = await InAsync(rig, c.CharacterId, c.ShipId, CharacterShips.CargoFlag, Tritanium, 6_000);

        var refused = Assert.IsType<ShipRefused>(await rig.Ships.MoveInSpaceAsync(c.CharacterId, hold, c.ShipId, can.Id, 6_000));

        // 5,499,950 x 0.01 = 54,999.5 of 55,000: 0.5 are left, 6,000 x 0.01 = 60 are asked for
        Assert.Equal((ShipRefusal.NoSpaceForThat, 0.5, 60d, Tritanium), (refused.Reason, refused.Available, refused.Volume, refused.ModuleTypeId));
        Assert.Equal(c.ShipId, (await rig.Items.GetAsync(hold))!.LocationId);
        var room = Assert.NotNull(await rig.Ships.ContainerRoomAsync(can.Id));
        Assert.Equal((55_000d, 54_999.5), (room.Capacity, Math.Round(room.Used, 6)));
        Assert.NotNull(await rig.Items.GetAsync(filler));
    }

    [PgFact]
    public async Task Goods_go_between_the_ship_and_a_container_only_and_only_from_where_they_are()
    {
        await using var rig = await Rig.CreateAsync();
        var (c, can) = await InSpaceWithACanAsync(rig);
        var inCan = await InAsync(rig, c.CharacterId, can.Id, CharacterShips.ContainerFlag, Tritanium, 5);

        // the ship to itself, the can to itself, an item that is not where it is said to be, a module fitted in a slot
        Assert.IsType<NothingHappened>(await rig.Ships.MoveInSpaceAsync(c.CharacterId, inCan, can.Id, can.Id, null));
        Assert.IsType<NothingHappened>(await rig.Ships.MoveInSpaceAsync(c.CharacterId, inCan, c.ShipId, can.Id, null));
        Assert.IsType<NothingHappened>(await rig.Ships.MoveInSpaceAsync(c.CharacterId, c.ShipId + 1, c.ShipId, can.Id, null));
        Assert.IsType<NothingHappened>(await rig.Ships.MoveInSpaceAsync(c.CharacterId, inCan, can.Id, c.ShipId, 6)); // more than there is
        Assert.Equal(can.Id, (await rig.Items.GetAsync(inCan))!.LocationId);
    }

    [PgFact]
    public async Task Removing_a_container_in_the_caller_transaction_recurses_and_preserves_moved_out_subtrees()
    {
        await using var rig = await Rig.CreateAsync();
        var (owner, root) = await InSpaceWithACanAsync(rig);
        var external = await rig.Ships.CreateCanAsync(owner.CharacterId, checked((int)root.LocationId));
        var otherOwner = await rig.NewCharacterAsync(name: $"ContainerOwner-{Guid.NewGuid():N}"[..24]);

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var nestedId = await rig.Items.CreateAsync(connection, transaction,
                new NewItem(CharacterShips.CargoContainerTypeId, otherOwner.CharacterId, root.Id, CharacterShips.ContainerFlag, 1, true));
            var nestedLootId = await rig.Items.CreateAsync(connection, transaction,
                new NewItem(Tritanium, otherOwner.CharacterId, nestedId, CharacterShips.ContainerFlag, 5, false));
            var movedOutId = await rig.Items.CreateAsync(connection, transaction,
                new NewItem(CharacterShips.CargoContainerTypeId, otherOwner.CharacterId, root.Id, CharacterShips.ContainerFlag, 1, true));
            var movedOutLootId = await rig.Items.CreateAsync(connection, transaction,
                new NewItem(Tritanium, otherOwner.CharacterId, movedOutId, CharacterShips.ContainerFlag, 3, false));
            await rig.Items.MoveAsync(connection, transaction, movedOutId, external.Id, CharacterShips.ContainerFlag);

            Assert.NotNull(await rig.Items.LockAndGetAsync(connection, transaction, root.Id));
            var removed = await rig.Ships.RemoveContainerAsync(connection, transaction, root.Id, withContents: true);

            Assert.Equal(root.Id, removed!.Id);
            Assert.Null(await rig.Items.LockAndGetAsync(connection, transaction, root.Id));
            Assert.Null(await rig.Items.LockAndGetAsync(connection, transaction, nestedId));
            Assert.Null(await rig.Items.LockAndGetAsync(connection, transaction, nestedLootId));
            Assert.NotNull(await rig.Items.LockAndGetAsync(connection, transaction, movedOutId));
            Assert.NotNull(await rig.Items.LockAndGetAsync(connection, transaction, movedOutLootId));
            await transaction.RollbackAsync();
        }

        Assert.NotNull(await rig.Items.GetAsync(root.Id));
        Assert.NotNull(await rig.Items.GetAsync(external.Id));
    }

    [PgFact]
    public async Task Removing_a_container_fails_safely_on_a_containment_cycle()
    {
        await using var rig = await Rig.CreateAsync();
        var (owner, root) = await InSpaceWithACanAsync(rig);
        var nestedOwner = await rig.NewCharacterAsync(name: $"CycleOwner-{Guid.NewGuid():N}"[..20]);

        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var nestedId = await rig.Items.CreateAsync(connection, transaction,
            new NewItem(CharacterShips.CargoContainerTypeId, nestedOwner.CharacterId, root.Id, CharacterShips.ContainerFlag, 1, true));
        await rig.Items.MoveAsync(connection, transaction, root.Id, nestedId, CharacterShips.ContainerFlag);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rig.Ships.RemoveContainerAsync(connection, transaction, root.Id, withContents: true));
        await transaction.RollbackAsync();

        Assert.NotNull(await rig.Items.GetAsync(root.Id));
        Assert.Null(await rig.Items.GetAsync(nestedId));
    }

    [PgFact]
    public async Task Removing_a_can_takes_what_is_in_it_only_when_asked_to()
    {
        await using var rig = await Rig.CreateAsync();
        var (c, can) = await InSpaceWithACanAsync(rig);
        var inCan = await InAsync(rig, c.CharacterId, can.Id, CharacterShips.ContainerFlag, Tritanium, 5);

        Assert.NotNull(await rig.Ships.RemoveContainerAsync(can.Id, withContents: false));
        Assert.Null(await rig.Items.GetAsync(can.Id));
        Assert.NotNull(await rig.Items.GetAsync(inCan)); // left where it was: nothing sees it any more, which is what a can that is emptied has
        Assert.Null(await rig.Ships.RemoveContainerAsync(can.Id, withContents: true));

        var populatedCan = await rig.Ships.CreateCanAsync(c.CharacterId, checked((int)can.LocationId));
        var inPopulatedCan = await InAsync(rig, c.CharacterId, populatedCan.Id, CharacterShips.ContainerFlag, Tritanium, 5);
        Assert.NotNull(await rig.Ships.RemoveContainerAsync(populatedCan.Id, withContents: true));
        Assert.Null(await rig.Items.GetAsync(inPopulatedCan));
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
