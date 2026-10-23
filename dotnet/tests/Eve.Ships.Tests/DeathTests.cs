using Eve.Inventory;

namespace Eve.Ships.Tests;

/// <summary>A ship that is destroyed: what it held is dropped into a wreck or is gone by the reference's rules, and its pilot is in a capsule.</summary>
public class DeathTests
{
    private const int FrigateWreck = 26506;
    private const int ValueImplant = 13166; // Inherent Implants 'Lancer' Gunnery RF-903 in pinned SDE 3396210.

    private const int Ibis = 601;

    private static async Task<(Rig Rig, Eve.Characters.Created Character)> InSpaceAsync(int shipTypeId = Ibis)
    {
        var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync(shipTypeId: shipTypeId);
        await rig.Ships.UndockAsync(character.CharacterId);
        return (rig, character);
    }

    /// <summary>A seed under which the item with this id drops (or does not).</summary>
    private static string SeedWhere(params (long Id, bool Drops)[] items) =>
        Enumerable.Range(0, 10_000).Select(n => $"probe:{n}").First(seed => items.All(item => DeathOutcome.HashToDrop(seed, item.Id, 0) == item.Drops));

    [PgFact]
    public async Task Run_space_wreck_provenance_is_committed_with_the_death_and_found_by_exact_scope()
    {
        var (rig, character) = await InSpaceAsync();
        await using var _ = rig;
        var systemId = checked((int)(await rig.Characters.GetAsync(character.CharacterId))!.SolarSystemId);
        var scope = new RunSpaceItemScope(Guid.NewGuid(), 95101, 2, systemId, character.CharacterId, "ship-death:95101");

        var death = await rig.Ships.DestroyShipAsync(
            character.CharacterId, character.ShipId, FrigateWreck, "seed", runSpaceScope: scope);

        Assert.NotNull(death?.Wreck);
        var wreck = (await rig.Items.GetAsync(death.Wreck.Id))!;
        Assert.Equal(scope, wreck.RunSpaceProvenance!.Scope);
        Assert.Equal(RunSpaceItemRole.Wreck, wreck.RunSpaceProvenance.Role);
        Assert.Equal(character.CharacterId, wreck.RunSpaceProvenance.SourceCharacterId);
        Assert.Equal(character.ShipId, wreck.RunSpaceProvenance.SourceShipId);
        Assert.Equal(character.ShipId, wreck.RunSpaceProvenance.SourceItemId);

        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var exact = await rig.Items.FindRunSpaceItemsAsync(connection, transaction, scope);
        Assert.Single(exact);
        Assert.Equal(wreck.Id, exact[0].Id);
        Assert.Equal(RunSpaceItemRole.Wreck, exact[0].RunSpaceProvenance!.Role);
        await transaction.CommitAsync();
    }

    [PgFact]
    public async Task DestroyShip_publishes_transition_within_the_death_transaction()
    {
        var (rig, character) = await InSpaceAsync();
        await using var _ = rig;
        Eve.Ships.ShipTransitionEvent? observed = null;
        rig.Ships.TransitionEventWriter = (connection, transaction, transition, _) =>
        {
            Assert.Same(connection, transaction.Connection);
            observed = transition;
            return Task.CompletedTask;
        };

        var death = await rig.Ships.DestroyShipAsync(character.CharacterId, character.ShipId, FrigateWreck, "transition");
        Assert.NotNull(death);
        var system = (await rig.Characters.GetAsync(character.CharacterId))!.SolarSystemId;
        Assert.Equal(new Eve.Ships.ShipTransitionEvent(
            "ship-destroyed", character.CharacterId, character.ShipId, death.Character.ShipId,
            system, CharacterShips.InSpaceFlag, system, CharacterShips.InSpaceFlag), observed);
    }

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
        var capsule = (await rig.Items.GetAsync(death.Capsule!.Id))!;
        Assert.Equal((CharacterShips.CapsuleTypeId, c.CharacterId, system, CharacterShips.InSpaceFlag, true), (capsule.TypeId, capsule.OwnerId, capsule.LocationId, capsule.Flag, capsule.Singleton));
        Assert.Equal(capsule.Id, (await rig.Characters.GetAsync(c.CharacterId))!.ShipId);
        Assert.Equal(capsule.Id, death.Character.ShipId);
        var wreck = (await rig.Items.GetAsync(death.Wreck!.Id))!;
        Assert.Equal((FrigateWreck, c.CharacterId, system, CharacterShips.InSpaceFlag, true), (wreck.TypeId, wreck.OwnerId, wreck.LocationId, wreck.Flag, wreck.Singleton));
    }


    [PgFact]
    public async Task A_valid_frigate_escape_bay_ship_survives_battleship_death_and_is_not_wreck_loot()
    {
        await using var rig = await Rig.CreateAsync();
        var escapeCapacityId = rig.Statics.AttributeId("frigateEscapeBayCapacity");
        Assert.NotNull(escapeCapacityId);
        var battleship = (await rig.Statics.TypesInGroupsAsync(["Battleship"]))
            .First(type => type.Attributes.GetValueOrDefault(escapeCapacityId.Value) > 0);
        var frigate = (await rig.Statics.TypesInGroupsAsync(["Frigate", "Assault Frigate", "Electronic Attack Ship", "Logistics Frigate"]))
            .First(type => type.CategoryId == 6);

        var character = await rig.NewCharacterAsync(shipTypeId: battleship.TypeId);
        await rig.Ships.UndockAsync(character.CharacterId);
        var system = (await rig.Characters.GetAsync(character.CharacterId))!.SolarSystemId;
        foreach (var skillId in frigate.RequiredSkills)
            await rig.SetSkillAsync(character.CharacterId, skillId, 5);
        var frigateId = await rig.GiveAsync(character.CharacterId, frigate.TypeId, singleton: true,
            location: character.ShipId, flag: CharacterShips.FrigateEscapeBayFlag);
        var cargoModuleId = await rig.GiveAsync(character.CharacterId, Rig.Afterburner, singleton: true,
            location: frigateId, flag: CharacterShips.CargoFlag);

        var death = await rig.Ships.DestroyShipAsync(character.CharacterId, character.ShipId, FrigateWreck, "escape-bay");
        Assert.NotNull(death);
        Assert.Null(death.Capsule);
        Assert.Equal(frigateId, death.EscapeShip!.Id);
        Assert.Equal(frigateId, death.PilotShip.Id);
        Assert.Equal(frigateId, (await rig.Characters.GetAsync(character.CharacterId))!.ShipId);
        var survivor = (await rig.Items.GetAsync(frigateId))!;
        Assert.Equal((character.CharacterId, system, CharacterShips.InSpaceFlag), (survivor.OwnerId, survivor.LocationId, survivor.Flag));
        Assert.DoesNotContain(death.Changes, change => change.Item.Id == frigateId || change.Item.Id == cargoModuleId);
        Assert.DoesNotContain((await rig.Items.InContainerAsync(death.Wreck!.Id)).Select(item => item.Id), id => id == frigateId || id == cargoModuleId);
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
        var c = await rig.NewCharacterAsync(shipTypeId: Ibis);
        Assert.Null(await rig.Ships.DestroyShipAsync(c.CharacterId, c.ShipId, FrigateWreck, "seed")); // docked
        await rig.Ships.UndockAsync(c.CharacterId);
        Assert.Null(await rig.Ships.DestroyShipAsync(c.CharacterId, c.ShipId + 12345, FrigateWreck, "seed")); // not the active ship
        Assert.Null(await rig.Ships.DestroyShipAsync(999, c.ShipId, FrigateWreck, "seed")); // not a character
        Assert.NotNull(await rig.Ships.DestroyShipAsync(c.CharacterId, c.ShipId, FrigateWreck, "seed"));
        Assert.Null(await rig.Ships.DestroyShipAsync(c.CharacterId, c.ShipId, FrigateWreck, "seed")); // once
    }


    [PgFact]
    public async Task Destroying_an_abandoned_active_hull_persists_one_capsule_wreck_and_cargo_outcome()
    {
        var (rig, character) = await InSpaceAsync();
        await using var _ = rig;
        var system = (await rig.Characters.GetAsync(character.CharacterId))!.SolarSystemId;
        var cargo = await rig.GiveAsync(character.CharacterId, Rig.Afterburner, true, 1,
            character.ShipId, CharacterShips.CargoFlag);
        var seed = SeedWhere((cargo, true));

        Assert.Null(await rig.Ships.DestroyAbandonedShipAsync(
            character.CharacterId, system + 1, character.ShipId, FrigateWreck, seed));
        Assert.Equal(character.ShipId, (await rig.Characters.GetAsync(character.CharacterId))!.ShipId);

        Assert.Null(await rig.Ships.DestroyAbandonedShipAsync(
            character.CharacterId, system, character.ShipId, FrigateWreck, seed));
        var death = await rig.Ships.DestroyAbandonedShipAsync(
            character.CharacterId, system, character.ShipId, FrigateWreck, seed, replaceActiveShip: true);

        Assert.NotNull(death);
        Assert.Null(await rig.Items.GetAsync(character.ShipId));
        Assert.Equal(CharacterShips.CapsuleTypeId, death.Capsule!.TypeId);
        Assert.Equal(death.Capsule.Id, (await rig.Characters.GetAsync(character.CharacterId))!.ShipId);
        Assert.Equal(death.Capsule.Id, (await rig.Ships.LoadAsync(character.CharacterId))!.Ship.Id);
        Assert.Equal((system, CharacterShips.InSpaceFlag), (death.Capsule.LocationId, death.Capsule.Flag));
        var wreckContents = await rig.Items.InContainerAsync(death.Wreck!.Id);
        Assert.Contains(wreckContents, item => item.Id == cargo);
        Assert.Empty(await rig.Items.InContainerAsync(character.ShipId));

        Assert.Null(await rig.Ships.DestroyAbandonedShipAsync(
            character.CharacterId, system, character.ShipId, FrigateWreck, seed));
        var itemsInSystem = await rig.Items.InLocationAsync(system, character.CharacterId);
        Assert.Single(itemsInSystem, item => item.TypeId == FrigateWreck);
        Assert.Equal(1, (await rig.Items.InContainerAsync(death.Wreck.Id)).Count(item => item.Id == cargo));
    }

    private const int CorpseMale = 25;
    private const int Cockroach = 11019;

    /// <summary>A character in a capsule in space (its ship was destroyed), and where its clone is.</summary>
    private static async Task<(Rig Rig, Eve.Characters.Created Character, long CapsuleId, HomePlace Home)> InCapsuleAsync()
    {
        var (rig, c) = await InSpaceAsync();
        var character = (await rig.Characters.GetAsync(c.CharacterId))!;
        var death = (await rig.Ships.DestroyShipAsync(c.CharacterId, c.ShipId, FrigateWreck, "seed"))!;
        return (rig, c, death.Capsule!.Id, new HomePlace(Rig.Station, character.SolarSystemId, character.ConstellationId, character.RegionId));
    }

    [PgFact]
    public async Task DestroyCapsule_publishes_transition_to_the_home_station_in_the_same_transaction()
    {
        var (rig, character, capsuleId, home) = await InCapsuleAsync();
        await using var _ = rig;
        Eve.Ships.ShipTransitionEvent? observed = null;
        rig.Ships.TransitionEventWriter = (connection, transaction, transition, _) =>
        {
            Assert.Same(connection, transaction.Connection);
            observed = transition;
            return Task.CompletedTask;
        };

        var death = await rig.Ships.DestroyCapsuleAsync(
            character.CharacterId, capsuleId, CorpseMale, "Ship Tester", Cockroach, home);
        Assert.NotNull(death);
        Assert.Equal(new Eve.Ships.ShipTransitionEvent(
            "capsule-destroyed", character.CharacterId, capsuleId, death.Ship.Id,
            home.SolarSystemId, CharacterShips.InSpaceFlag, home.StationId, ItemFlags.Hangar), observed);
    }

    [PgFact]
    public async Task Run_space_corpse_provenance_is_exact_even_with_the_normal_zero_flag()
    {
        var (rig, character, capsuleId, home) = await InCapsuleAsync();
        await using var _ = rig;
        var scope = new RunSpaceItemScope(Guid.NewGuid(), 95102, 3, home.SolarSystemId, character.CharacterId, "pod-death:95102");

        var death = await rig.Ships.DestroyCapsuleAsync(
            character.CharacterId, capsuleId, CorpseMale, "Abyssal Pilot", Cockroach, home, runSpaceScope: scope);

        Assert.NotNull(death?.Corpse);
        var corpse = (await rig.Items.GetAsync(death.Corpse.Id))!;
        Assert.Equal(0, corpse.Flag);
        Assert.Equal(scope, corpse.RunSpaceProvenance!.Scope);
        Assert.Equal(RunSpaceItemRole.Corpse, corpse.RunSpaceProvenance.Role);
        Assert.Equal(character.CharacterId, corpse.RunSpaceProvenance.SourceCharacterId);
        Assert.Equal(capsuleId, corpse.RunSpaceProvenance.SourceShipId);
        Assert.Equal(capsuleId, corpse.RunSpaceProvenance.SourceItemId);

        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var exact = await rig.Items.FindRunSpaceItemsAsync(connection, transaction, scope);
        Assert.Single(exact);
        Assert.Equal(corpse.Id, exact[0].Id);
        Assert.Equal(0, exact[0].Flag);
        await transaction.CommitAsync();
    }

    [PgFact]
    public async Task A_pilot_whose_capsule_is_destroyed_wakes_in_the_Cockroach_at_the_home_station_and_leaves_a_corpse_where_the_capsule_was()
    {
        var (rig, c, capsule, home) = await InCapsuleAsync();
        await using var _ = rig;
        var system = home.SolarSystemId;

        var pod = await rig.Ships.DestroyCapsuleAsync(c.CharacterId, capsule, CorpseMale, "Ship Tester", Cockroach, home);

        Assert.NotNull(pod);
        Assert.Null(await rig.Items.GetAsync(capsule));
        var corpse = (await rig.Items.GetAsync(pod.Corpse!.Id))!;
        Assert.Equal((CorpseMale, c.CharacterId, (long)system, 0, "Ship Tester", true), (corpse.TypeId, corpse.OwnerId, corpse.LocationId, corpse.Flag, corpse.CustomName, corpse.Singleton));
        var ship = (await rig.Items.GetAsync(pod.Ship.Id))!;
        Assert.Equal((Cockroach, c.CharacterId, Rig.Station, ItemFlags.Hangar), (ship.TypeId, ship.OwnerId, ship.LocationId, ship.Flag));
        Assert.Empty(await rig.InShipAsync(c.CharacterId, ship.Id));
        var woke = (await rig.Characters.GetAsync(c.CharacterId))!;
        Assert.Equal((ship.Id, Rig.Station), (woke.ShipId, woke.StationId));
        Assert.Equal((ship.Id, Rig.Station), (pod.Character.ShipId, pod.Character.StationId));
    }

    [PgFact]
    public async Task A_clone_in_another_solar_system_moves_the_pilot_there_and_no_kind_of_corpse_leaves_none()
    {
        var (rig, c, capsule, _) = await InCapsuleAsync();
        await using var _ = rig;
        var elsewhere = new HomePlace(60003760, 30000142, 20000020, 10000002);

        var pod = (await rig.Ships.DestroyCapsuleAsync(c.CharacterId, capsule, null, "Ship Tester", Cockroach, elsewhere))!;

        Assert.Null(pod.Corpse);
        var woke = (await rig.Characters.GetAsync(c.CharacterId))!;
        Assert.Equal((60003760L, 30000142, 20000020, 10000002), (woke.StationId, woke.SolarSystemId, woke.ConstellationId, woke.RegionId));
        Assert.Equal((60003760L, 30000142), (pod.Character.StationId, pod.Character.SolarSystemId));
    }

    [PgFact]
    public async Task Capsule_contents_and_destructible_implants_are_destroyed_with_it()
    {
        var (rig, c, capsule, home) = await InCapsuleAsync();
        await using var _ = rig;
        await rig.Statics.EnsureTypesAsync([ValueImplant]);
        var left = await rig.GiveAsync(c.CharacterId, 34, false, 10, capsule, CharacterShips.CargoFlag);
        var implant = await rig.GiveAsync(c.CharacterId, ValueImplant, true, 1, c.CharacterId, PilotAugmentations.ImplantFlag);

        var pod = (await rig.Ships.DestroyCapsuleAsync(c.CharacterId, capsule, CorpseMale, "Ship Tester", Cockroach, home))!;

        Assert.Equal([left], pod.Held.Select(i => i.Id));
        Assert.Equal([implant], pod.Implants.Select(i => i.Id));
        Assert.Null(await rig.Items.GetAsync(left));
        Assert.Null(await rig.Items.GetAsync(implant));
    }

    [PgFact]
    public async Task Only_the_characters_capsule_in_space_is_destroyed_that_way_and_a_ship_is_not()
    {
        // a ship that is alive is not a capsule
        var (rig0, c0) = await InSpaceAsync();
        await using (rig0)
        {
            var place = (await rig0.Characters.GetAsync(c0.CharacterId))!;
            var away = new HomePlace(Rig.Station, place.SolarSystemId, place.ConstellationId, place.RegionId);
            Assert.Null(await rig0.Ships.DestroyCapsuleAsync(c0.CharacterId, c0.ShipId, CorpseMale, "x", Cockroach, away));
            Assert.NotNull(await rig0.Items.GetAsync(c0.ShipId));
        }

        var (rig, c, capsule, home) = await InCapsuleAsync();
        await using var _ = rig;
        Assert.Null(await rig.Ships.DestroyShipAsync(c.CharacterId, capsule, FrigateWreck, "seed")); // a capsule is not destroyed as a ship is
        Assert.Null(await rig.Ships.DestroyCapsuleAsync(999, capsule, CorpseMale, "x", Cockroach, home)); // not a character
        Assert.NotNull(await rig.Ships.DestroyCapsuleAsync(c.CharacterId, capsule, CorpseMale, "x", Cockroach, home));
        Assert.Null(await rig.Ships.DestroyCapsuleAsync(c.CharacterId, capsule, CorpseMale, "x", Cockroach, home)); // once
    }
}
