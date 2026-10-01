using Eve.Characters;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
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
        var placing = Assert.IsType<SimPlaceContainer>(rig.Sim.Heard[heard]);
        Assert.Equal(((ulong)flying.Session.SessionId, 23, "Cargo Container", 1176, 275.0), (placing.SessionId, placing.Kind.TypeId, placing.Kind.Name, placing.Kind.GraphicId, placing.Distance));
        Assert.Equal((flying.Character.ShipId, flying.Character.CharacterId), (placing.LauncherId, placing.Credit.CharacterId));
        Assert.Equal(0, placing.Credit.WarFactionId);
        Assert.Equal(new SimChangeWreck((ulong)flying.Session.SessionId, placing.ContainerId, false), rig.Sim.Heard[heard + 1]);
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
        // a pilot that is in another corporation takes nothing from the can (a new character is in the corporation of its school: they are the same here, so the owner is changed)
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
        Assert.Equal((system, can, 12, kept.LauncherId, false), (placed.SolarSystemId, placed.WreckId, placed.GroupId, placed.LauncherId, placed.IsEmpty));
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
        // a record of an item that is not there any more, and the can's contents taken out by the owner outside the host
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
}
