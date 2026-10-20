using Eve.Characters;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Planets;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>The cans and wrecks beside a pilot's ship: opened from where the ship is, jettisoned into, taken from and put in, and left to expire.</summary>
public class SpaceContainerTests
{
    private const int Tritanium = 34, Pyerite = 35;

    private sealed record Flying(Rig Rig, ClientSession Session, Created Character, long Stack);

    /// <summary>A pilot in space with <paramref name="quantity"/> tritanium in its hold.</summary>
    private static async Task<Flying> FlyingWithCargoAsync(Rig rig, long quantity = 100, int type = Tritanium)
    {
        var character = await rig.NewCharacterAsync();
        var stack = (await rig.Ships.GrantAsync(character.CharacterId, type, quantity, singleton: false))!.Single().After;
        Assert.IsType<Eve.Ships.ShipChanged>(await rig.Ships.MoveAsync(character.CharacterId, stack.Id, Rig.Station, character.ShipId, Eve.Ships.CharacterShips.CargoFlag, null));
        var session = await rig.SessionAsync(character, 1);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        await rig.BindBeyonceAsync(session);
        Events.Of(session);
        return new Flying(rig, session, character, stack.Id);
    }

    private static async Task<long> DroneInSpaceAsync(Rig rig, long characterId, int system, int droneTypeId)
    {
        var drone = (await rig.Ships.GrantAsync(characterId, droneTypeId, 1, singleton: true))!.Single().After;
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await rig.Items.MoveAsync(connection, transaction, drone.Id, system, Eve.Ships.CharacterShips.InSpaceFlag);
        await transaction.CommitAsync();
        return drone.Id;
    }

    private static async Task<(string Oid, PyValue Answer)> JettisonAsync(Flying flying, params long[] ids)
    {
        var oid = await flying.Rig.BindShipInSpaceAsync(flying.Session);
        return (oid, await flying.Rig.ShipCallAsync(flying.Session, "Jettison", oid, new PyList([.. ids.Select(InventoryWire.Id)])));
    }

    private static (string Name, Dictionary<string, PyValue> Values) UserError(MachoWrappedException error)
    {
        var args = ((PyTuple)((PyObjectEx)error.Error.Payload).Header is PyTuple header ? (PyTuple)header.Items[1] : throw new InvalidOperationException()).Items;
        return (JsSemantics.StringValue(args[0])!, ((PyDict)args[1]).Entries.ToDictionary(e => JsSemantics.StringValue(e.Key)!, e => e.Value));
    }

    [PgFact]
    public async Task Cargo_that_is_jettisoned_goes_into_a_new_can_ahead_of_the_ship_and_the_client_is_told_what_left_the_hold()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await FlyingWithCargoAsync(rig);
        var system = flying.Session.GetInt("solarsystemid")!.Value;
        var heard = rig.Sim.Heard.Count;

        var (_, answer) = await JettisonAsync(flying, flying.Stack);

        // (the ids that went into a can, the ids launched as cans of their own)
        var (jettisoned, launched) = Assert.IsType<PyTuple>(answer).Items switch { [PyTuple a, PyTuple b] => (a, b), var other => throw new InvalidOperationException(string.Join(",", other)) };
        Assert.Equal([Events.Number(InventoryWire.Id(flying.Stack))], jettisoned.Items.Select(Events.Number));
        Assert.Empty(launched.Items);
        // the scene is asked for a can ahead of the ship, then told something is in it
        var placing = Assert.IsType<SimPlaceContainer>(rig.Sim.Heard.ToArray()[heard]);
        Assert.Equal(((ulong)flying.Session.SessionId, 23, "Cargo Container", 1176, 275.0), (placing.SessionId, placing.Kind.TypeId, placing.Kind.Name, placing.Kind.GraphicId, placing.Distance));
        Assert.Equal((flying.Character.ShipId, flying.Character.CharacterId), (placing.LauncherId, placing.Credit.CharacterId));
        Assert.Equal(0, placing.Credit.WarFactionId);
        Assert.Equal(new SimChangeWreck((ulong)flying.Session.SessionId, placing.ContainerId, false), rig.Sim.Heard.ToArray()[heard + 1]);
        // the can is an item in the solar system and the stack is in it with the id it had
        var can = (await rig.Items.GetAsync(placing.ContainerId))!;
        Assert.Equal((23, flying.Character.CharacterId, system, 0), (can.TypeId, can.OwnerId, can.LocationId, can.Flag));
        var stack = (await rig.Items.GetAsync(flying.Stack))!;
        Assert.Equal((can.Id, 0, 100L), (stack.LocationId, stack.Flag, stack.Quantity));
        // the client is told the stack left the hold, as the reference tells it (OnItemsChanged for the ship, what it was: the place and the flag)
        var told = Events.Of(flying.Session).Single(e => e.Name == "OnItemsChanged");
        Assert.Equal("shipid", told.IdType);
        var changes = (PyDict)told.Args[1];
        Assert.Equal([(3L, flying.Character.ShipId), (4L, 5L)], changes.Entries.Select(e => (Events.Number(e.Key), Events.Number(e.Value))));
        Assert.IsType<PyNone>(told.Args[2]);
    }

    [PgFact]
    public async Task A_second_can_is_not_made_for_three_minutes_and_the_pilot_is_told_how_long_is_left()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await FlyingWithCargoAsync(rig);
        var other = (await rig.Ships.GrantAsync(flying.Character.CharacterId, Pyerite, 10, singleton: false))!.Single().After;
        await JettisonAsync(flying, flying.Stack);
        rig.Now += 55_000;

        var refused = await Assert.ThrowsAsync<MachoWrappedException>(async () => await JettisonAsync(flying, other.Id));

        var (name, values) = UserError(refused);
        Assert.Equal("ShpJettisonPending", name);
        Assert.Equal("2 minutes and 5 seconds", JsSemantics.StringValue(values["eta"]));
        rig.Now += 125_000;
        // the three minutes are up: a can is made (the pyerite is in the station, not the hold: nothing goes, and the can that was made for it goes again)
        Assert.Empty(((PyTuple)((PyTuple)(await JettisonAsync(flying, other.Id)).Answer).Items[0]).Items);
    }

    [PgFact]
    public async Task A_can_the_scene_will_not_take_is_taken_back_and_nothing_leaves_the_hold()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await FlyingWithCargoAsync(rig);
        rig.Sim.PlaceContainerAnswer = new CommandReply(false, 0, "NO_SHIP_IN_SPACE");

        var (_, answer) = await JettisonAsync(flying, flying.Stack);

        Assert.Empty(((PyTuple)((PyTuple)answer).Items[0]).Items);
        var system = flying.Session.GetInt("solarsystemid")!.Value;
        Assert.DoesNotContain(await rig.Items.InContainerAsync(system), i => i.TypeId == 23);
        Assert.Equal(flying.Character.ShipId, (await rig.Items.GetAsync(flying.Stack))!.LocationId);
        // the pilot is not made to wait for a can it did not get
        rig.Sim.PlaceContainerAnswer = null;
        Assert.Single(((PyTuple)((PyTuple)(await JettisonAsync(flying, flying.Stack)).Answer).Items[0]).Items);
    }

    [PgFact]
    public async Task What_is_not_in_the_hold_or_not_the_pilots_is_not_jettisoned()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await FlyingWithCargoAsync(rig);
        var fitted = flying.Character.ShipId + 1;

        var (_, answer) = await JettisonAsync(flying, fitted, 12345);

        Assert.Empty(((PyTuple)((PyTuple)answer).Items[0]).Items);
        Assert.Equal(flying.Character.ShipId, (await rig.Items.GetAsync(fitted))!.LocationId);
        Assert.DoesNotContain(await rig.Items.InContainerAsync(flying.Session.GetInt("solarsystemid")!.Value), i => i.TypeId == 23);
    }

    private static async Task<(Flying Flying, long Can)> WithACanAsync(Rig rig, long quantity = 100)
    {
        var flying = await FlyingWithCargoAsync(rig, quantity);
        await JettisonAsync(flying, flying.Stack);
        var can = rig.Sim.Heard.OfType<SimPlaceContainer>().Last().ContainerId;
        Events.Of(flying.Session);
        return (flying, can);
    }

    [PgFact]
    public async Task A_pilot_opens_a_can_it_sees_within_range_and_is_refused_one_that_is_far_or_not_in_view()
    {
        await using var rig = await Rig.CreateAsync();
        var (flying, can) = await WithACanAsync(rig);

        Assert.Equal(can, (await rig.Containers.OpenAsync(flying.Session, can, CancellationToken.None))!.Id);
        // not an item that is a container, not an item at all
        Assert.Null(await rig.Containers.OpenAsync(flying.Session, flying.Character.ShipId, CancellationToken.None));
        Assert.Null(await rig.Containers.OpenAsync(flying.Session, 12345, CancellationToken.None));
        rig.Sim.MeasureAnswer = 2501;
        Assert.Equal("TargetTooFar", UserError(await Assert.ThrowsAsync<MachoWrappedException>(async () => await rig.Containers.OpenAsync(flying.Session, can, CancellationToken.None))).Name);
        rig.Sim.MeasureAnswer = 2500;
        Assert.NotNull(await rig.Containers.OpenAsync(flying.Session, can, CancellationToken.None));
        rig.Sim.MeasureAnswer = null;
        Assert.Equal("FakeItemNotFound", UserError(await Assert.ThrowsAsync<MachoWrappedException>(async () => await rig.Containers.OpenAsync(flying.Session, can, CancellationToken.None))).Name);
    }

    [PgFact]
    public async Task A_customs_office_is_opened_through_the_same_in_space_range_check_as_other_containers()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await FlyingWithCargoAsync(rig);
        long officeId;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            officeId = await rig.Items.CreateAsync(connection, transaction,
                new NewItem(Customs.OfficeTypeId, 1_000_125, flying.Session.GetInt("solarsystemid")!.Value, 0, 1, true));
            await transaction.CommitAsync();
        }

        var shipId = flying.Session.GetInt("shipid")!.Value;
        rig.Sim.MeasureAnswer = 2500;
        Assert.Equal(officeId, (await rig.Containers.OpenAsync(flying.Session, officeId, CancellationToken.None))!.Id);
        Assert.Contains(rig.Sim.Heard.OfType<SimCommand>(), command =>
            command.Code == SimCommands.Measure &&
            command.Args.SequenceEqual(new[] { (double)officeId, (double)shipId }));
        rig.Sim.MeasureAnswer = 2501;
        Assert.Equal("TargetTooFar", UserError(await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await rig.Containers.OpenAsync(flying.Session, officeId, CancellationToken.None))).Name);
    }

    [PgFact]
    public async Task What_is_taken_out_of_a_can_splits_into_the_hold_and_the_client_is_told_in_the_reference_order()
    {
        await using var rig = await Rig.CreateAsync();
        var (flying, can) = await WithACanAsync(rig, 95);
        var ship = flying.Character.ShipId;
        var heard = rig.Sim.Heard.Count;

        var moved = await rig.Containers.MoveAsync(flying.Session, flying.Stack, can, ship, 25, CancellationToken.None);

        Assert.NotEqual(flying.Stack, moved);
        Assert.Equal(70, (await rig.Items.GetAsync(flying.Stack))!.Quantity);
        var taken = (await rig.Items.GetAsync(moved!.Value))!;
        Assert.Equal((ship, Eve.Ships.CharacterShips.CargoFlag, 25L, flying.Character.CharacterId), (taken.LocationId, taken.Flag, taken.Quantity, taken.OwnerId));
        var events = Events.Of(flying.Session);
        // two stacks, each with the place the loot window shows them in, then the refresh of the window
        Assert.Equal(["OnItemsChanged", "OnItemsChanged", "OnClientEvent_MoveFromCargoToHangar"], events.Select(e => e.Name));
        var context = Assert.IsType<PyTuple>(events[0].Args[2]);
        Assert.Equal(("Ship", ship, "ShipCargo"), (JsSemantics.StringValue(context.Items[0]), Events.Number(context.Items[1]), JsSemantics.StringValue(context.Items[2])));
        Assert.Equal([(9L, 95L)], ((PyDict)events[0].Args[1]).Entries.Select(e => (Events.Number(e.Key), Events.Number(e.Value))));
        Assert.Equal([(3L, 0L), (9L, 0L)], ((PyDict)events[1].Args[1]).Entries.Select(e => (Events.Number(e.Key), Events.Number(e.Value))));
        Assert.Equal([can, ship, 5], events[2].Args.Select(Events.Number));
        // the scene is told what is left in the can, once, after the move
        Assert.Equal(new SimChangeWreck((ulong)flying.Session.SessionId, can, false), Assert.Single(rig.Sim.Heard.Skip(heard).OfType<SimChangeWreck>()));
    }

    [PgFact]
    public async Task A_can_that_is_emptied_goes_at_once_with_its_ball_and_a_can_that_is_not_the_pilots_is_not_emptied_while_the_safety_is_on()
    {
        await using var rig = await Rig.CreateAsync();
        var (flying, can) = await WithACanAsync(rig, 40);
        var other = await rig.NewCharacterAsync("Other Pilot", 2);

        await rig.Ships.GrantAsync(other.CharacterId, Tritanium, 1, singleton: false);

        var moved = await rig.Containers.MoveAsync(flying.Session, flying.Stack, can, flying.Character.ShipId, 40, CancellationToken.None);

        Assert.Equal(flying.Stack, moved);
        Assert.Null(await rig.Items.GetAsync(can));
        var events = Events.Of(flying.Session);
        Assert.Equal(["OnItemsChanged", "OnClientEvent_MoveFromCargoToHangar", "OnItemsChanged"], events.Select(e => e.Name));
        // the can's own removal is told to the character, the item as gone to the junkyard
        Assert.Equal("charid", events[2].IdType);
        Assert.Equal(new SimRemoveWreck(can, 3), rig.Sim.Heard.Last());
    }

    [PgFact]
    public async Task Goods_put_in_a_can_are_the_pilots_still_and_what_does_not_fit_is_refused_with_the_room()
    {
        await using var rig = await Rig.CreateAsync();
        var (flying, can) = await WithACanAsync(rig, 10);
        var ship = flying.Character.ShipId;
        var more = (await rig.Ships.GrantAsync(flying.Character.CharacterId, Pyerite, 50, singleton: false))!.Single().After;
        // it is in the station: put it in the hold by moving it there (the pilot is in space, so a stack made in the hold)
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Items.MoveAsync(connection, transaction, more.Id, ship, Eve.Ships.CharacterShips.CargoFlag);
            await transaction.CommitAsync();
        }
        var heard = rig.Sim.Heard.Count;

        var put = await rig.Containers.MoveAsync(flying.Session, more.Id, ship, can, 20, CancellationToken.None);

        var inside = (await rig.Items.GetAsync(put!.Value))!;
        Assert.Equal((can, 0, 20L, flying.Character.CharacterId), (inside.LocationId, inside.Flag, inside.Quantity, inside.OwnerId));
        Assert.Equal(30, (await rig.Items.GetAsync(more.Id))!.Quantity);
        // what is put in is told as a stack that changed (the reference's OnItemChange), twice
        Assert.Equal(["OnItemChange", "OnItemChange"], Events.Of(flying.Session).Select(e => e.Name));
        // the can was not empty: the scene needs no word
        Assert.DoesNotContain(rig.Sim.Heard.Skip(heard), m => m is SimChangeWreck);
    }

    [PgFact]
    public async Task A_jettisoned_can_is_kept_where_it_lies_for_two_hours_and_goes_with_what_is_in_it_when_they_are_up()
    {
        await using var rig = await Rig.CreateAsync();
        var (flying, can) = await WithACanAsync(rig);

        var kept = (await rig.SpaceItems.GetAsync(can))!;
        Assert.Equal([1000.0, 2000.0, 3000.0], kept.Position);
        Assert.Equal([1.0, 0.0, 0.0], kept.Direction);
        Assert.Equal((12, flying.Character.ShipId), (kept.GroupId, kept.LauncherId));
        Assert.Equal((flying.Character.CharacterId, Rig.ClockMs + (long)SpaceContainers.CanLifetime.TotalMilliseconds), (kept.CharacterId, kept.ExpiresAtMs));

        await rig.Containers.ExpireDueAsync(Rig.ClockMs + (long)SpaceContainers.CanLifetime.TotalMilliseconds - 1);
        Assert.NotNull(await rig.Items.GetAsync(can));
        await rig.Containers.ExpireDueAsync(Rig.ClockMs + (long)SpaceContainers.CanLifetime.TotalMilliseconds);

        Assert.Null(await rig.Items.GetAsync(can));
        Assert.Null(await rig.Items.GetAsync(flying.Stack));
        Assert.Null(await rig.SpaceItems.GetAsync(can));
        Assert.Equal(new SimRemoveWreck(can, 0), rig.Sim.Heard.Last());
    }

    [PgFact]
    public async Task A_can_the_simulation_lost_is_put_back_when_a_ship_comes_into_the_scene_of_its_system_and_only_once()
    {
        await using var rig = await Rig.CreateAsync();
        var (flying, can) = await WithACanAsync(rig);
        var system = (int)flying.Session.GetInt("solarsystemid")!.Value;
        var kept = (await rig.SpaceItems.GetAsync(can))!;
        var heard = rig.Sim.Heard.Count;

        // the simulation has it: nothing is put back
        await rig.Containers.RestoreAsync(system);
        Assert.Equal(heard, rig.Sim.Heard.Count);

        // the simulation is lost and a scene is made again
        rig.Sim.Lose();
        await rig.Containers.RestoreAsync(system);
        var placed = Assert.IsType<SimPlaceWreck>(Assert.Single(rig.Sim.Heard.Skip(heard)));
        Assert.Equal((system, can, 12, kept.LauncherId, false), (placed.Scene.LocationId, placed.WreckId, placed.GroupId, placed.LauncherId, placed.IsEmpty));
        Assert.Equal(new SimWreckKind(23, "Cargo Container", 1176), placed.Kind);
        Assert.Equal([1000.0, 2000.0, 3000.0], placed.Position);
        Assert.Equal(14.0, placed.Radius);
        Assert.Equal(new SimCredit(flying.Character.CharacterId, kept.CorporationId, kept.AllianceId, 0), placed.Credit);
        await rig.Containers.RestoreAsync(system);
        Assert.Single(rig.Sim.Heard.Skip(heard));
        // another system has nothing to put back
        await rig.Containers.RestoreAsync(system + 1);
        Assert.Single(rig.Sim.Heard.Skip(heard));
    }

    [PgFact]
    public async Task What_is_put_back_is_empty_when_nothing_is_in_it_and_what_ran_out_of_time_or_is_gone_is_forgotten_instead()
    {
        await using var rig = await Rig.CreateAsync();
        var (flying, can) = await WithACanAsync(rig);
        var system = (int)flying.Session.GetInt("solarsystemid")!.Value;

        await rig.SpaceItems.SaveAsync(new Eve.Ships.SpaceItem(777_000_001, system, [0, 0, 0], [1, 0, 0], 14, 12, 0, 0, 0, 0, 0, Rig.ClockMs + 1_000_000));
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Items.DeleteAsync(connection, transaction, flying.Stack);
            await transaction.CommitAsync();
        }
        rig.Sim.Lose();
        var heard = rig.Sim.Heard.Count;

        await rig.Containers.RestoreAsync(system);

        var placed = Assert.IsType<SimPlaceWreck>(Assert.Single(rig.Sim.Heard.Skip(heard)));
        Assert.Equal((can, true), (placed.WreckId, placed.IsEmpty));
        Assert.Null(await rig.SpaceItems.GetAsync(777_000_001));
        // time runs out while the simulation does not have it: it goes instead of being put back
        rig.Sim.Lose();
        rig.Now = Rig.ClockMs + (long)SpaceContainers.CanLifetime.TotalMilliseconds + 1;
        await rig.Containers.RestoreAsync(system);
        Assert.Single(rig.Sim.Heard.Skip(heard));
        Assert.Null(await rig.Items.GetAsync(can));
        Assert.Null(await rig.SpaceItems.GetAsync(can));
    }

    [PgFact]
    public async Task A_can_that_is_emptied_is_forgotten_with_its_place()
    {
        await using var rig = await Rig.CreateAsync();
        var (flying, can) = await WithACanAsync(rig, 40);

        await rig.Containers.MoveAsync(flying.Session, flying.Stack, can, flying.Character.ShipId, 40, CancellationToken.None);

        Assert.Null(await rig.SpaceItems.GetAsync(can));
    }

    [PgFact]
    public async Task Saved_asteroid_items_are_restored_to_the_simulation_once()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await FlyingWithCargoAsync(rig);
        var system = (int)flying.Session.GetInt("solarsystemid")!.Value;
        var veldspar = Assert.Single(await new Eve.Sde.SdeReader(rig.Source).ReadTypeSummariesAsync(),
            type => type.Published && type.Name == "Veldspar").TypeId;
        var (asteroid, _) = await rig.Ships.CreateAsteroidAsync(veldspar, system, 0, 1200, [1000, 2000, 3000], [1, 0, 0], 25, rig.SpaceItems);
        var type = rig.Statics.Type(asteroid.TypeId)!;
        var heard = rig.Sim.Heard.Count;
        rig.Sim.Lose();

        await rig.Containers.RestoreAsync(system);

        var placed = Assert.IsType<Eve.Space.SimPlaceAsteroid>(Assert.Single(rig.Sim.Heard.Skip(heard)));
        Assert.Equal((system, asteroid.Id, asteroid.TypeId, type.GroupId, type.CategoryId, type.Name, type.GraphicId, asteroid.Quantity),
            (placed.SolarSystemId, placed.AsteroidId, placed.TypeId, placed.GroupId, placed.CategoryId, placed.Name, placed.GraphicId, placed.UnitsRemaining));
        await rig.Containers.RestoreAsync(system);
        Assert.Single(rig.Sim.Heard.Skip(heard));
    }

    [PgFact]
    public async Task A_belt_arrival_seeds_each_ore_its_system_security_holds_as_a_persisted_sde_asteroid()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await FlyingWithCargoAsync(rig);
        var system = (int)flying.Session.GetInt("solarsystemid")!.Value;
        const long beltId = 9_000_000_001;
        var ores = Eve.Ships.BeltOres.For(rig.Systems.Get(system)!.Security);

        rig.Sim.Say(new Eve.Space.SimArrived(
            (ulong)flying.Session.SessionId, beltId, [1000, 2000, 3000]));
        await Rig.UntilAsync(() => ores.All(ore => rig.Sim.Heard.OfType<Eve.Space.SimPlaceAsteroid>().Any(p => p.Name == ore)));

        var placed = rig.Sim.Heard.OfType<Eve.Space.SimPlaceAsteroid>().Where(p => ores.Contains(p.Name)).ToList();
        Assert.Equal(ores.Count, placed.Count);
        foreach (var rock in placed)
        {
            Assert.Equal(system, rock.SolarSystemId);
            Assert.True(rock.AsteroidId > 0);
            var saved = Assert.Single(await rig.SpaceItems.InSystemAsync(system), spaceItem => spaceItem.ItemId == rock.AsteroidId);
            Assert.Equal(beltId, saved.LauncherId);
            var asteroid = (await rig.Items.GetAsync(saved.ItemId))!;
            var unitVolume = rig.Statics.Type(asteroid.TypeId)!.Attributes[rig.Statics.AttributeId("volume")!.Value];
            Assert.Equal(Eve.Ships.BeltOres.UnitsFor(unitVolume), asteroid.Quantity);
            Assert.Equal(asteroid.Quantity, rock.UnitsRemaining);
        }
    }

    [PgFact]
    public async Task A_belt_whose_rocks_are_all_depleted_is_seeded_again_when_a_pilot_arrives()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await FlyingWithCargoAsync(rig);
        var system = (int)flying.Session.GetInt("solarsystemid")!.Value;
        const long beltId = 9_000_000_002;
        var types = await new Eve.Sde.SdeReader(rig.Source).ReadTypeSummariesAsync();
        var veldspar = Assert.Single(types, type => type.Published && type.Name == "Veldspar").TypeId;
        var ores = Eve.Ships.BeltOres.For(rig.Systems.Get(system)!.Security);
        var (depleted, _) = await rig.Ships.CreateAsteroidAsync(veldspar, system, beltId, 1000, [1000, 2000, 3000], [1, 0, 0], 25, rig.SpaceItems);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Items.SetQuantityAsync(connection, transaction, depleted.Id, 0);
            await transaction.CommitAsync();
        }

        rig.Sim.Say(new Eve.Space.SimArrived((ulong)flying.Session.SessionId, beltId, [1000, 2000, 3000]));
        await Rig.UntilAsync(() => ores.All(ore => rig.Sim.Heard.OfType<Eve.Space.SimPlaceAsteroid>().Any(p => p.Name == ore)));

        var placed = rig.Sim.Heard.OfType<Eve.Space.SimPlaceAsteroid>().Where(p => ores.Contains(p.Name)).ToList();
        Assert.Equal(ores.Count, placed.Count);
        Assert.DoesNotContain(placed, p => p.AsteroidId == depleted.Id);
    }

    [PgFact]
    public async Task A_belt_arrival_seeds_a_harvestable_gas_cloud_beside_its_rock()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await FlyingWithCargoAsync(rig);
        var system = (int)flying.Session.GetInt("solarsystemid")!.Value;
        const long beltId = 9_000_000_003;
        bool IsCloud(Eve.Space.SimPlaceAsteroid p) => rig.Statics.GroupName(rig.Statics.Type(p.TypeId)!.GroupId) == "Harvestable Cloud";

        rig.Sim.Say(new Eve.Space.SimArrived((ulong)flying.Session.SessionId, beltId, [1000, 2000, 3000]));
        await Rig.UntilAsync(() => rig.Sim.Heard.OfType<Eve.Space.SimPlaceAsteroid>().Any(IsCloud));

        var cloud = Assert.Single(rig.Sim.Heard.OfType<Eve.Space.SimPlaceAsteroid>(), IsCloud);
        Assert.Equal((system, 1_000L), (cloud.SolarSystemId, cloud.UnitsRemaining));
        var saved = Assert.Single(await rig.SpaceItems.InSystemAsync(system), spaceItem => spaceItem.ItemId == cloud.AsteroidId);
        Assert.Equal(beltId, saved.LauncherId);
        var item = (await rig.Items.GetAsync(saved.ItemId))!;
        Assert.Equal("Harvestable Cloud", rig.Statics.GroupName(rig.Statics.Type(item.TypeId)!.GroupId));
    }

    [PgFact]
    public async Task Mine_cycles_check_drone_ownership_and_range_then_persist_ore_and_notify_the_pilot()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await FlyingWithCargoAsync(rig);
        var system = (int)flying.Session.GetInt("solarsystemid")!.Value;
        var types = await new Eve.Sde.SdeReader(rig.Source).ReadTypeSummariesAsync();
        var veldspar = Assert.Single(types, type => type.Published && type.Name == "Veldspar").TypeId;
        var miningDroneI = Assert.Single(types, type => type.Published && type.Name == "Mining Drone I").TypeId;
        var (asteroid, _) = await rig.Ships.CreateAsteroidAsync(veldspar, system, 0, 1000, [1000, 2000, 3000], [1, 0, 0], 25, rig.SpaceItems);
        var other = await rig.NewCharacterAsync("Other Pilot", 2);
        var unownedDrone = await DroneInSpaceAsync(rig, other.CharacterId, system, miningDroneI);
        var drone = await DroneInSpaceAsync(rig, flying.Character.CharacterId, system, miningDroneI);
        Eve.Space.SimDroneWorkCycle Cycle(long droneId, ulong cycleId) => new(
            (ulong)flying.Session.SessionId, flying.Character.CharacterId, flying.Character.ShipId, system, droneId, asteroid.Id, cycleId,
            Eve.Space.SimDroneCommands.MineRepeatedly, 25, 0, 0, 0, 0.5);

        rig.Sim.Say(Cycle(unownedDrone, 10));
        await Rig.UntilAsync(() => rig.Sim.Heard.OfType<Eve.Space.SimDroneWorkResult>().Any(r => r.CycleId == 10));
        var ownership = rig.Sim.Heard.OfType<Eve.Space.SimDroneWorkResult>().Single(r => r.CycleId == 10);
        Assert.False(ownership.Accepted);
        Assert.Equal("NOT_CONTROLLED", ownership.Reason);
        Assert.Equal(1000, (await rig.Items.GetAsync(asteroid.Id))!.Quantity);

        rig.Sim.MeasureAnswer = 30_001;
        rig.Sim.Say(Cycle(drone, 11));
        await Rig.UntilAsync(() => rig.Sim.Heard.OfType<Eve.Space.SimDroneWorkResult>().Any(r => r.CycleId == 11));
        var range = rig.Sim.Heard.OfType<Eve.Space.SimDroneWorkResult>().Single(r => r.CycleId == 11);
        Assert.False(range.Accepted);
        Assert.Equal("OUT_OF_RANGE", range.Reason);
        Assert.Equal(1000, (await rig.Items.GetAsync(asteroid.Id))!.Quantity);

        rig.Sim.MeasureAnswer = 100;
        rig.Sim.Say(Cycle(drone, 12));
        await Rig.UntilAsync(() => rig.Sim.Heard.OfType<Eve.Space.SimDroneWorkResult>().Any(r => r.CycleId == 12));
        var mined = rig.Sim.Heard.OfType<Eve.Space.SimDroneWorkResult>().Single(r => r.CycleId == 12);
        Assert.True(mined.Accepted);
        Assert.Equal((750L, 250L, false), (mined.UnitsRemaining, mined.DeliveredUnits, mined.Complete));
        Assert.Equal(750, (await rig.Items.GetAsync(asteroid.Id))!.Quantity);
        Assert.Equal(250L, Assert.Single(await rig.Items.InContainerAsync(flying.Character.ShipId), i => i.TypeId == veldspar).Quantity);
        Assert.Contains(Events.Of(flying.Session), e => e.Name == "OnItemChange");
    }

    [PgFact]
    public async Task Salvage_cycles_retry_failed_access_and_replace_the_wreck_without_losing_its_contents()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await FlyingWithCargoAsync(rig);
        var system = (int)flying.Session.GetInt("solarsystemid")!.Value;
        var sde = new Eve.Sde.SdeReader(rig.Source);
        var types = await sde.ReadTypeSummariesAsync();
        var factions = await sde.ReadAsync("factions");
        var amarrFactionId = (int)Assert.Single(factions, faction =>
            faction.Doc.TryGetProperty("name", out var name)
            && Eve.Sde.SdeReader.English(name).Equals("Amarr Empire", StringComparison.OrdinalIgnoreCase)).Key;
        var wreckTypeId = Assert.Single(types, type => type.Name == "Amarr Frigate Wreck").TypeId;
        var salvageDroneI = Assert.Single(types, type => type.Published && type.Name == "Salvage Drone I").TypeId;
        await rig.Statics.EnsureTypesAsync([wreckTypeId, Tritanium, salvageDroneI]);
        var wreckType = rig.Statics.Type(wreckTypeId)!;
        long wreckId, lootId;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            wreckId = await rig.Items.CreateAsync(connection, transaction,
                new Eve.Inventory.NewItem(wreckTypeId, flying.Character.CharacterId, system, Eve.Ships.CharacterShips.InSpaceFlag, 1, true));
            lootId = await rig.Items.CreateAsync(connection, transaction,
                new Eve.Inventory.NewItem(Tritanium, flying.Character.CharacterId, wreckId, 0, 5, false));
            await transaction.CommitAsync();
        }
        await rig.SpaceItems.SaveAsync(new Eve.Ships.SpaceItem(
            wreckId, system, [1000, 2000, 3000], [1, 0, 0], 14, wreckType.GroupId, flying.Character.ShipId,
            flying.Character.CharacterId, 0, 0, amarrFactionId, long.MaxValue));
        var drone = await DroneInSpaceAsync(rig, flying.Character.CharacterId, system, salvageDroneI);
        Eve.Space.SimDroneWorkCycle Cycle(ulong cycleId, double roll) => new(
            (ulong)flying.Session.SessionId, flying.Character.CharacterId, flying.Character.ShipId, system, drone, wreckId, cycleId,
            Eve.Space.SimDroneCommands.Salvage, 0, 0, 0, 3, roll);

        rig.Sim.Say(Cycle(20, 0.99));
        await Rig.UntilAsync(() => rig.Sim.Heard.OfType<Eve.Space.SimDroneWorkResult>().Any(r => r.CycleId == 20));
        var missed = rig.Sim.Heard.OfType<Eve.Space.SimDroneWorkResult>().Single(r => r.CycleId == 20);
        Assert.True(missed.Accepted);
        Assert.False(missed.Complete);
        Assert.NotNull(await rig.Items.GetAsync(wreckId));

        var heard = rig.Sim.Heard.Count;
        rig.Sim.Say(Cycle(21, 0.0));
        await Rig.UntilAsync(() => rig.Sim.Heard.OfType<Eve.Space.SimDroneWorkResult>().Any(r => r.CycleId == 21));
        var salvaged = rig.Sim.Heard.OfType<Eve.Space.SimDroneWorkResult>().Single(r => r.CycleId == 21);
        Assert.True(salvaged.Accepted);
        Assert.True(salvaged.Complete);
        Assert.Equal(1L, salvaged.DeliveredUnits);
        var product = Assert.Single(await rig.Items.InContainerAsync(flying.Character.ShipId), item =>
            rig.Statics.GroupName(rig.Statics.Type(item.TypeId)!.GroupId) == "Salvaged Materials");
        Assert.Equal(1L, product.Quantity);
        var productTypeName = rig.Statics.Type(product.TypeId)!.Name;
        Assert.Contains(productTypeName, new[]
        {
            "Armor Plates", "Burned Logic Circuit", "Charred Micro Circuit", "Contaminated Nanite Compound",
            "Defective Current Pump", "Fried Interface Circuit", "Melted Capacitor Console",
            "Tangled Power Conduit", "Tripped Power Circuit",
        });
        Assert.Null(await rig.Items.GetAsync(wreckId));
        Assert.Null(await rig.SpaceItems.GetAsync(wreckId));
        var replacement = Assert.IsType<Eve.Space.SimPlaceWreck>(Assert.Single(rig.Sim.Heard.Skip(heard).OfType<Eve.Space.SimPlaceWreck>()));
        var can = (await rig.Items.GetAsync(replacement.WreckId))!;
        Assert.Equal(lootId, Assert.Single(await rig.Items.InContainerAsync(can.Id)).Id);
        Assert.Equal(can.Id, (await rig.SpaceItems.GetAsync(can.Id))!.ItemId);
        Assert.Contains(Events.Of(flying.Session), e => e.Name == "OnItemChange");
    }
}
