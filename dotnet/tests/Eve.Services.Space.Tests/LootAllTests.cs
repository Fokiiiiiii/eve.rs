using Eve.Characters;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// "Loot All" (<c>invbroker</c> <c>MultiAdd</c>, recorded in <c>loot.json</c>): everything asked for goes from a can or a wreck into the hold at once, all or nothing for room, and the client is told
/// once for the batch what it is told for each item of a single move.
/// </summary>
public class LootAllTests
{
    private const int Tritanium = 34, Pyerite = 35;

    private sealed record Flying(Rig Rig, ClientSession Session, Created Character, long First, long Second, long Can);

    /// <summary>A pilot in space whose tritanium (100) and pyerite (60) are jettisoned into one can.</summary>
    private static async Task<Flying> WithACanAsync(Rig rig, long tritanium = 100, long pyerite = 60)
    {
        var character = await rig.NewCharacterAsync();
        var first = (await rig.Ships.GrantAsync(character.CharacterId, Tritanium, tritanium, singleton: false))!.Single().After;
        var second = (await rig.Ships.GrantAsync(character.CharacterId, Pyerite, pyerite, singleton: false))!.Single().After;
        foreach (var stack in new[] { first, second })
        {
            Assert.IsType<Eve.Ships.ShipChanged>(await rig.Ships.MoveAsync(character.CharacterId, stack.Id, Rig.Station, character.ShipId, Eve.Ships.CharacterShips.CargoFlag, null));
        }
        var session = await rig.SessionAsync(character, 1);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        await rig.BindBeyonceAsync(session);
        var space = await rig.BindShipInSpaceAsync(session);
        await rig.ShipCallAsync(session, "Jettison", space, new PyList([InventoryWire.Id(first.Id), InventoryWire.Id(second.Id)]));
        var can = rig.Sim.Heard.OfType<SimPlaceContainer>().Last().ContainerId;
        Events.Of(session);
        return new Flying(rig, session, character, first.Id, second.Id, can);
    }

    private static (string Name, Dictionary<string, PyValue> Values) UserError(MachoWrappedException error)
    {
        var args = ((PyTuple)((PyObjectEx)error.Error.Payload).Header is PyTuple header ? (PyTuple)header.Items[1] : throw new InvalidOperationException()).Items;
        return (JsSemantics.StringValue(args[0])!, ((PyDict)args[1]).Entries.ToDictionary(e => JsSemantics.StringValue(e.Key)!, e => e.Value));
    }

    private static Task<bool?> LootAsync(Flying f, params long[] ids) =>
        f.Rig.Containers.MoveAllAsync(f.Session, ids, f.Can, f.Character.ShipId, CancellationToken.None);

    [PgFact]
    public async Task Every_stack_goes_whole_into_the_hold_and_the_client_is_told_in_the_recorded_order_with_a_single_word_for_the_whole_batch()
    {
        await using var rig = await Rig.CreateAsync();
        var f = await WithACanAsync(rig);
        var heard = rig.Sim.Heard.Count;

        Assert.True(await LootAsync(f, f.First, f.Second));

        // the stacks keep their ids and are the pilot's, in the hold
        var ship = f.Character.ShipId;
        foreach (var (id, type, quantity) in new[] { (f.First, Tritanium, 100L), (f.Second, Pyerite, 60L) })
        {
            var item = (await rig.Items.GetAsync(id))!;
            Assert.Equal((type, f.Character.CharacterId, ship, Eve.Ships.CharacterShips.CargoFlag, quantity), (item.TypeId, item.OwnerId, item.LocationId, item.Flag, item.Quantity));
        }
        // the can is empty and goes: the item is gone and the scene is told, after it was told twice what is in it (once for each stack, as the reference does)
        Assert.Null(await rig.Items.GetAsync(f.Can));
        Assert.Equal(
            [new SimChangeWreck((ulong)f.Session.SessionId, f.Can, true), new SimChangeWreck((ulong)f.Session.SessionId, f.Can, true), new SimRemoveWreck(f.Can, 3)],
            rig.Sim.Heard.Skip(heard).OfType<SimMessage>().Where(m => m is SimChangeWreck or SimRemoveWreck));

        var events = Events.Of(f.Session);
        Assert.Equal(
            ["OnItemsChanged", "OnItemsChanged", "OnClientEvent_MoveFromCargoToHangar", "OnWreckLootAll", "OnItemsChanged"],
            events.Select(e => e.Name));
        // each stack as it was before: the place it left and the flag, to the character, with no place for the loot window
        foreach (var (e, id) in events.Take(2).Zip(new[] { f.First, f.Second }))
        {
            Assert.Equal("charid", e.IdType);
            Assert.Equal([(3L, f.Can), (4L, 0L)], ((PyDict)e.Args[1]).Entries.Select(x => (Events.Number(x.Key), Events.Number(x.Value))));
            Assert.IsType<PyNone>(e.Args[2]);
        }
        Assert.Equal([f.Can, ship, 5], events[2].Args.Select(Events.Number));
        // what the loot window closes over: the can and each item as it was in it
        var all = events[3];
        Assert.Equal("clientID", all.IdType);
        var window = (PyTuple)all.Args[0];
        Assert.Equal(("ItemFloatingCargo", f.Can), (JsSemantics.StringValue(window.Items[0]), Events.Number(window.Items[1])));
        var rows = ((PyTuple)all.Args[1]).Items.Cast<PyTuple>().ToList();
        Assert.Equal(
            [(f.First, Tritanium, f.Character.CharacterId, f.Can, 0L, 100L), (f.Second, Pyerite, f.Character.CharacterId, f.Can, 0L, 60L)],
            rows.Select(r => (Events.Number(r.Items[0]), (int)Events.Number(r.Items[1]), Events.Number(r.Items[2]), Events.Number(r.Items[3]), Events.Number(r.Items[4]), Events.Number(r.Items[5]))));
        // the can that goes: the item to the junkyard, its row without custom info
        Assert.Equal("charid", events[4].IdType);
        Assert.IsType<PyNone>(((PyPackedRow)((PyList)events[4].Args[0]).Items[0]).Values[^1]);
    }

    [PgFact]
    public async Task What_does_not_fit_together_is_refused_whole_with_the_room_that_is_left_and_the_room_it_takes_and_nothing_moves()
    {
        await using var rig = await Rig.CreateAsync();
        // 11,000 tritanium (110 m3) and 1,400 pyerite (14 m3) fit the hold of 125 together only when nothing else is in it: put 20 m3 of something else in first
        var f = await WithACanAsync(rig, tritanium: 11_000, pyerite: 1_400);
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var filler = await rig.Items.CreateAsync(connection, transaction, new NewItem(Tritanium, f.Character.CharacterId, f.Character.ShipId, Eve.Ships.CharacterShips.CargoFlag, 2_000));
        await transaction.CommitAsync();
        Events.Of(f.Session);
        var heard = rig.Sim.Heard.Count;

        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => LootAsync(f, f.First, f.Second));

        var (name, values) = UserError(refused);
        Assert.Equal("NotEnoughCargoSpace", name);
        // 125 - 20 = 105 left; the batch takes 110 + 14 = 124
        Assert.Equal((105L, 124L), (Events.Number(values["available"]), Events.Number(values["volume"])));
        Assert.Equal(f.Can, (await rig.Items.GetAsync(f.First))!.LocationId);
        Assert.Equal(f.Can, (await rig.Items.GetAsync(f.Second))!.LocationId);
        Assert.NotNull(await rig.Items.GetAsync(f.Can));
        Assert.Empty(Events.Of(f.Session));
        Assert.Empty(rig.Sim.Heard.Skip(heard).OfType<SimChangeWreck>());
        Assert.Equal(filler, (await rig.Items.GetAsync(filler))!.Id);
    }

    [PgFact]
    public async Task An_id_that_is_not_in_the_can_is_left_out_and_when_none_is_in_it_nothing_happens()
    {
        await using var rig = await Rig.CreateAsync();
        var f = await WithACanAsync(rig);

        // one of the two and an id that is nothing: the one goes, the can keeps the other, and the window is not closed (nothing in "OnWreckLootAll")
        Assert.True(await LootAsync(f, f.First, 424242, f.First));

        Assert.Equal(f.Character.ShipId, (await rig.Items.GetAsync(f.First))!.LocationId);
        Assert.Equal(f.Can, (await rig.Items.GetAsync(f.Second))!.LocationId);
        Assert.Equal(["OnItemsChanged", "OnClientEvent_MoveFromCargoToHangar"], Events.Of(f.Session).Select(e => e.Name));
        Assert.NotNull(await rig.Items.GetAsync(f.Can));

        Assert.False(await LootAsync(f, 424242, f.First));
        Assert.Empty(Events.Of(f.Session));
    }

    [PgFact]
    public async Task A_container_that_cannot_be_opened_is_nothing_and_one_that_is_far_is_too_far()
    {
        await using var rig = await Rig.CreateAsync();
        var f = await WithACanAsync(rig);

        // not a container of the scene
        Assert.Null(await rig.Containers.MoveAllAsync(f.Session, [f.First], f.Character.ShipId, f.Character.ShipId, CancellationToken.None));
        Assert.Null(await rig.Containers.MoveAllAsync(f.Session, [f.First], 12345, f.Character.ShipId, CancellationToken.None));
        // somewhere else than the pilot's own ship
        Assert.Null(await rig.Containers.MoveAllAsync(f.Session, [f.First], f.Can, f.Can, CancellationToken.None));
        // far from it
        rig.Sim.MeasureAnswer = 2501;
        Assert.Equal("TargetTooFar", UserError(await Assert.ThrowsAsync<MachoWrappedException>(() => LootAsync(f, f.First))).Name);
        rig.Sim.MeasureAnswer = 2500;
        Assert.Equal(f.Can, (await rig.Items.GetAsync(f.First))!.LocationId);
        Assert.Empty(Events.Of(f.Session));
    }

    [PgFact]
    public async Task What_is_in_a_can_that_is_not_the_pilots_or_its_corporations_is_not_taken_while_the_safety_is_on()
    {
        await using var rig = await Rig.CreateAsync();
        var f = await WithACanAsync(rig);
        // the can is somebody else's (nobody the pilot shares a corporation with)
        var can = (await rig.Items.GetAsync(f.Can))!;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Items.MoveToOwnerAsync(connection, transaction, can.Id, 777_000_001, can.LocationId, can.Flag);
            await transaction.CommitAsync();
        }

        Assert.Equal("SafetyActivated", UserError(await Assert.ThrowsAsync<MachoWrappedException>(() => LootAsync(f, f.First, f.Second))).Name);

        Assert.Equal(f.Can, (await rig.Items.GetAsync(f.First))!.LocationId);
        Assert.Empty(Events.Of(f.Session));
    }


    [PgFact]
    public async Task Abandon_loot_requires_existing_rights_is_idempotent_and_keeps_the_contents()
    {
        await using var rig = await Rig.CreateAsync();
        var f = await WithACanAsync(rig);
        var container = (await rig.Items.GetAsync(f.Can))!;
        var entity = new Eve.Services.Space.EntityService(rig.Drones, () => Rig.ClockMs, rig.Containers);
        var bound = (PyTuple)await entity.MachoBindObject(new Eve.Services.ServiceCall
        {
            Method = "MachoBindObject",
            Args = [Py.Tuple(new PyInt(f.Session.GetInt("solarsystemid")!.Value), new PyInt(5)), PyNone.Instance],
            Session = f.Session,
        });
        var call = new Eve.Services.ServiceCall
        {
            Method = "CmdAbandonLoot",
            Args = [new PyList([new PyLong(f.Can), new PyLong(f.Can)])],
            Session = f.Session,
            BoundObjectId = Rig.OidOf(bound.Items[0]),
        };

        async Task SetContainerOwnerAsync(long ownerId)
        {
            await using var connection = await rig.Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await rig.Items.MoveToOwnerAsync(connection, transaction, container.Id, ownerId, container.LocationId, container.Flag);
            await transaction.CommitAsync();
        }

        await SetContainerOwnerAsync(777_000_001);
        var refused = await Assert.ThrowsAsync<MachoWrappedException>(async () => await entity.CmdAbandonLoot(call));
        Assert.Equal("SafetyActivated", UserError(refused).Name);
        Assert.Equal(f.Can, (await rig.Items.GetAsync(f.First))!.LocationId);

        await SetContainerOwnerAsync(f.Character.CharacterId);
        Assert.Equal(PyNone.Instance, await entity.CmdAbandonLoot(call));
        Assert.Equal(PyNone.Instance, await entity.CmdAbandonLoot(call));
        Assert.Equal(f.Can, (await rig.Items.GetAsync(f.First))!.LocationId);
        Assert.Equal(f.Can, (await rig.Items.GetAsync(f.Second))!.LocationId);

        await SetContainerOwnerAsync(777_000_001);
        Assert.Equal(PyNone.Instance, await entity.CmdAbandonLoot(call));
        Assert.Equal(true, await LootAsync(f, f.First, f.Second));
        Assert.Equal(f.Character.ShipId, (await rig.Items.GetAsync(f.First))!.LocationId);
        Assert.Equal(f.Character.ShipId, (await rig.Items.GetAsync(f.Second))!.LocationId);
    }
}
