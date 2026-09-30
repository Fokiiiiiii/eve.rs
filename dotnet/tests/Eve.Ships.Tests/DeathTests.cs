using Eve.Inventory;

namespace Eve.Ships.Tests;

/// <summary>A ship that is destroyed: what it held is dropped into a wreck or is gone by the reference's rules, and its pilot is in a capsule.</summary>
public class DeathTests
{
    private const int FrigateWreck = 26506;

    private static async Task<(Rig Rig, Eve.Characters.Created Character)> InSpaceAsync()
    {
        var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        await rig.Ships.UndockAsync(character.CharacterId);
        return (rig, character);
    }

    /// <summary>A seed under which the item with this id drops (or does not).</summary>
    private static string SeedWhere(params (long Id, bool Drops)[] items) =>
        Enumerable.Range(0, 10_000).Select(n => $"probe:{n}").First(seed => items.All(item => DeathOutcome.HashToDrop(seed, item.Id, 0) == item.Drops));

    [PgFact]
    public async Task A_destroyed_ship_is_gone_its_pilot_is_in_a_capsule_where_it_was_and_a_wreck_is_there_too()
    {
        var (rig, c) = await InSpaceAsync();
        await using var _ = rig;
        var system = (await rig.Characters.GetAsync(c.CharacterId))!.SolarSystemId;

        var death = await rig.Ships.DestroyShipAsync(c.CharacterId, c.ShipId, FrigateWreck, "seed");

        Assert.NotNull(death);
        Assert.Null(await rig.Items.GetAsync(c.ShipId));
        Assert.Equal(c.ShipId, death.Ship.Id);
        var capsule = (await rig.Items.GetAsync(death.Capsule.Id))!;
        Assert.Equal((CharacterShips.CapsuleTypeId, c.CharacterId, system, CharacterShips.InSpaceFlag, true), (capsule.TypeId, capsule.OwnerId, capsule.LocationId, capsule.Flag, capsule.Singleton));
        Assert.Equal(capsule.Id, (await rig.Characters.GetAsync(c.CharacterId))!.ShipId);
        Assert.Equal(capsule.Id, death.Character.ShipId);
        var wreck = (await rig.Items.GetAsync(death.Wreck!.Id))!;
        Assert.Equal((FrigateWreck, c.CharacterId, system, CharacterShips.InSpaceFlag, true), (wreck.TypeId, wreck.OwnerId, wreck.LocationId, wreck.Flag, wreck.Singleton));
    }

    [PgFact]
    public async Task What_a_ship_held_is_moved_into_the_wreck_or_destroyed_as_the_seed_says_and_each_module_is_accounted_for_once()
    {
        var (rig, c) = await InSpaceAsync();
        await using var _ = rig;
        var fitted = (await rig.Items.InLocationAsync(c.ShipId, c.CharacterId)).OrderBy(i => i.Id).ToList();
        Assert.Equal(3, fitted.Count);
        // the first module drops, the other two do not
        var seed = SeedWhere((fitted[0].Id, true), (fitted[1].Id, false), (fitted[2].Id, false));

        var death = (await rig.Ships.DestroyShipAsync(c.CharacterId, c.ShipId, FrigateWreck, seed))!;

        var inWreck = await rig.Items.InContainerAsync(death.Wreck!.Id);
        Assert.Equal([fitted[0].Id], inWreck.Select(i => i.Id));
        Assert.Equal(ItemFlags.Hangar, inWreck[0].Flag);
        var moved = Assert.Single(death.Moved);
        Assert.Equal((fitted[0].Id, c.ShipId, fitted[0].Flag), (moved.After.Id, moved.PreviousLocationId, moved.PreviousFlag));
        Assert.Equal((death.Wreck.Id, ItemFlags.Hangar), (moved.After.LocationId, moved.After.Flag));
        Assert.Equal([fitted[1].Id, fitted[2].Id], death.Destroyed.Select(i => i.Id).Order());
        Assert.Null(await rig.Items.GetAsync(fitted[1].Id));
        Assert.Null(await rig.Items.GetAsync(fitted[2].Id));
        Assert.Empty(await rig.Items.InContainerAsync(c.ShipId));
        // a module that dropped is not switched on any more
        Assert.Equal(Eve.Dogma.ItemState.Offline, (await rig.Modules.StatesAsync([fitted[0].Id]))[fitted[0].Id]);
    }

    [PgFact]
    public async Task Cargo_is_dropped_in_stacks_and_what_a_destroyed_container_held_is_taken_out_of_it()
    {
        var (rig, c) = await InSpaceAsync();
        await using var _ = rig;
        // a stack in the hold, and a container in the hold that holds a module
        var stack = await rig.GiveAsync(c.CharacterId, 34, false, 1000, c.ShipId, CharacterShips.CargoFlag);
        var container = await rig.GiveAsync(c.CharacterId, 3296, true, 1, c.ShipId, CharacterShips.CargoFlag);
        var inside = await rig.GiveAsync(c.CharacterId, Rig.Afterburner, true, 1, container, ItemFlags.Hangar);
        var fitted = (await rig.Items.InLocationAsync(c.ShipId, c.CharacterId)).Where(i => i.Id != stack && i.Id != container).ToList();
        var seed = SeedWhere((container, false), (inside, true), (fitted[0].Id, false), (fitted[1].Id, false), (fitted[2].Id, false));
        var (dropped, destroyed) = DeathOutcome.StackDropCounts(stack, CharacterShips.CargoFlag, 1000, seed);
        Assert.InRange(dropped, 1, 999);

        var death = (await rig.Ships.DestroyShipAsync(c.CharacterId, c.ShipId, FrigateWreck, seed))!;

        var inWreck = (await rig.Items.InContainerAsync(death.Wreck!.Id)).ToList();
        // the units of the stack that dropped are a stack of their own; the module that was in the destroyed container is taken out and dropped
        Assert.Equal(2, inWreck.Count);
        Assert.Equal(dropped, inWreck.Single(i => i.TypeId == 34).Quantity);
        Assert.Equal(inside, inWreck.Single(i => i.TypeId == Rig.Afterburner).Id);
        Assert.Null(await rig.Items.GetAsync(stack));
        Assert.Null(await rig.Items.GetAsync(container));
        var stackMove = death.Moved.Single(m => m.After.TypeId == 34);
        Assert.Equal((c.ShipId, CharacterShips.CargoFlag, dropped), (stackMove.PreviousLocationId, stackMove.PreviousFlag, stackMove.After.Quantity));
        Assert.Contains(death.Destroyed, i => i.Id == container);
        Assert.True(destroyed > 0);
        Assert.Equal(destroyed, death.Destroyed.Single(i => i.Id == stack).Quantity);
    }

    [PgFact]
    public async Task With_no_wreck_type_everything_is_destroyed_and_nothing_is_left()
    {
        var (rig, c) = await InSpaceAsync();
        await using var _ = rig;
        var container = await rig.GiveAsync(c.CharacterId, 3296, true, 1, c.ShipId, CharacterShips.CargoFlag);
        var inside = await rig.GiveAsync(c.CharacterId, Rig.Afterburner, true, 1, container, ItemFlags.Hangar);
        var death = (await rig.Ships.DestroyShipAsync(c.CharacterId, c.ShipId, null, SeedWhere((container, true), (inside, true))))!;
        Assert.Null(death.Wreck);
        Assert.Empty(death.Moved);
        Assert.Equal(5, death.Destroyed.Count);
        Assert.Contains(death.Destroyed, i => i.Id == inside);
        Assert.Empty(await rig.Items.InContainerAsync(c.ShipId));
        Assert.Empty(await rig.Items.InContainerAsync(container));
    }

    [PgFact]
    public async Task A_ship_that_is_docked_or_not_the_characters_active_ship_or_already_destroyed_is_not_destroyed()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        Assert.Null(await rig.Ships.DestroyShipAsync(c.CharacterId, c.ShipId, FrigateWreck, "seed")); // docked
        await rig.Ships.UndockAsync(c.CharacterId);
        Assert.Null(await rig.Ships.DestroyShipAsync(c.CharacterId, c.ShipId + 12345, FrigateWreck, "seed")); // not the active ship
        Assert.Null(await rig.Ships.DestroyShipAsync(999, c.ShipId, FrigateWreck, "seed")); // not a character
        Assert.NotNull(await rig.Ships.DestroyShipAsync(c.CharacterId, c.ShipId, FrigateWreck, "seed"));
        Assert.Null(await rig.Ships.DestroyShipAsync(c.CharacterId, c.ShipId, FrigateWreck, "seed")); // once
    }
}
