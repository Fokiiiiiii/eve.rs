using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Inventory;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Character_manager_mutations_persist_through_registered_host_wire()
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        var characterId = await rig.CreateCharacterAsync();
        var contactId = await rig.CreateCharacterAsync();
        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charMgr"));

        Start(configure: registry => registry.Register(service),
            sessionOpened: session => session.CharacterId = characterId);
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        Assert.Equal(PyNone.Instance, await CallOverHost(front, open, 1, "charMgr", "AddContact",
            Py.Tuple(new PyLong(contactId), new PyInt(-10), PyBool.True, PyBool.False, new PyStr(""))));
        var contacts = Assert.IsType<PyObject>(await CallOverHost(front, open, 2, "charMgr", "GetContactList", Py.Tuple([])));
        var addresses = PersonalWireField(contacts, "addresses");
        Assert.Equal(new PyList([new PyList([
            new PyInt(contactId), new PyInt(1), new PyInt(-10), new PyInt(0)])]),
            PersonalWireField(addresses, "lines"));

        var blockedIds = Enumerable.Range(0, 500).Select(id => (PyValue)new PyLong(90_100_000 + id)).ToArray();
        Assert.Equal(PyNone.Instance, await CallOverHost(front, open, 3, "charMgr", "BlockOwners",
            Py.Tuple(new PyList(blockedIds))));
        Assert.Equal(PyNone.Instance, await CallOverHost(front, open, 4, "charMgr", "EditContactsRelationshipID",
            Py.Tuple(new PyList([new PyLong(contactId)]), new PyInt(10))));
        contacts = Assert.IsType<PyObject>(await CallOverHost(front, open, 5, "charMgr", "GetContactList", Py.Tuple([])));
        addresses = PersonalWireField(contacts, "addresses");
        Assert.Equal(new PyList([new PyList([
            new PyInt(contactId), new PyInt(1), new PyInt(10), new PyInt(0)])]),
            PersonalWireField(addresses, "lines"));
        Assert.Equal(500, Assert.IsType<PyList>(PersonalWireField(PersonalWireField(contacts, "blocked"), "lines")).Items.Count);

        var deleteIds = Enumerable.Range(1, 500).Select(id => (PyValue)new PyLong(90_200_000 + id)).ToArray();
        deleteIds[0] = new PyLong(contactId);
        Assert.Equal(PyNone.Instance, await CallOverHost(front, open, 6, "charMgr", "DeleteContacts",
            Py.Tuple(new PyList(deleteIds))));
        contacts = Assert.IsType<PyObject>(await CallOverHost(front, open, 7, "charMgr", "GetContactList", Py.Tuple([])));
        Assert.Empty(Assert.IsType<PyList>(PersonalWireField(PersonalWireField(contacts, "addresses"), "lines")).Items);

        var noteId = Assert.IsType<PyInt>(await CallOverHost(front, open, 8, "charMgr", "AddOwnerNote",
            Py.Tuple(new PyStr("N:Planner"), new PyStr("<br>"))));
        Assert.True(noteId.Value > 0);
        Assert.Equal(PyNone.Instance, await CallOverHost(front, open, 9, "charMgr", "EditOwnerNote",
            Py.Tuple(noteId, new PyStr("N:Planner renamed"))));
        Assert.Equal(PyNone.Instance, await CallOverHost(front, open, 10, "charMgr", "EditOwnerNote",
            Py.Tuple(noteId, new PyStr("N:Planner"), new PyStr("client note"))));
        var noteRows = Assert.IsType<PyList>(await CallOverHost(front, open, 11, "charMgr", "GetOwnerNote", Py.Tuple(noteId)));
        Assert.Equal("N:Planner", T(PersonalWireField(Assert.Single(noteRows.Items), "label")));
        var note = PersonalWireField(Assert.Single(noteRows.Items), "note");
        Assert.IsType<PyBytes>(note);
        Assert.Equal("client note", T(note));

        Assert.Equal(PyNone.Instance, await CallOverHost(front, open, 12, "charMgr", "SaveCharacterSetting",
            Py.Tuple(new PyStr("windowState"), new PyStr("open"))));
        Assert.Equal(PyNone.Instance, await CallOverHost(front, open, 13, "charMgr", "DeleteCharacterSetting",
            Py.Tuple(new PyStr("windowState"))));
        Assert.Empty(Assert.IsType<PyDict>(await CallOverHost(front, open, 14, "charMgr", "GetCharacterSettings",
            Py.Tuple([]))).Entries);

        await rig.RestartAsync();
        service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charMgr"));
        var persisted = Assert.IsType<PyList>(await service.CallAsync(new ServiceCall
        {
            Method = "GetOwnerNote", Args = [noteId], Session = new ClientSession { CharacterId = characterId },
        }));
        Assert.Equal(new PyStr("client note"), PersonalWireField(Assert.Single(persisted.Items), "note"));
    }

    [PgFact]
    public async Task Bound_inventory_add_uses_registered_host_wire_with_client_arguments_and_transport_metadata()
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        var characterId = await rig.CreateCharacterAsync();
        var items = new PgItemStore(rig.Source, rig.InventorySchema);
        long shipId;
        long stackId;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            shipId = await items.CreateAsync(connection, transaction,
                new NewItem(587, characterId, rig.Station, 4, Singleton: true));
            await rig.Characters.SetShipAsync(connection, transaction, characterId, shipId);
            stackId = await items.CreateAsync(connection, transaction,
                new NewItem(34, characterId, rig.Station, 4, 10, Singleton: false));
            await transaction.CommitAsync();
        }

        var invBroker = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("invbroker"));
        Start(configure: registry => registry.Register(invBroker),
            sessionOpened: session =>
            {
                session.CharacterId = characterId;
                session.Set("charid", new PyLong(characterId));
                session.Set("stationid", new PyLong(rig.Station));
                session.Set("shipid", new PyLong(shipId));
            });
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var transport = new PyDict([new KeyValuePair<PyValue, PyValue>(
            new PyStr("machoVersion"), new PyInt(3_396_210))]);
        var nestedKwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("qty"), PyNone.Instance),
            new KeyValuePair<PyValue, PyValue>(new PyStr("flag"), new PyInt(5)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(3_396_210)),
        ]);
        var request = Packets.CallWithKwargs(open, 1, "invbroker", "MachoBindObject",
            Py.Tuple(new PyLong(shipId), Py.Tuple(
                new PyStr("Add"),
                Py.Tuple(new PyLong(stackId), new PyLong(rig.Station)),
                nestedKwargs)), transport);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(request)));
        PyValue response = PyNone.Instance;
        var sawNotification = false;
        for (var packetIndex = 0; packetIndex < 8 && response is PyNone; packetIndex++)
        {
            var packet = Assert.IsType<PyObject>(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload));
            var state = Assert.IsType<PyTuple>(packet.Args).Items;
            if (Assert.IsType<PyInt>(state[0]).Value == MachoMessage.Notification)
            {
                sawNotification = true;
                continue;
            }
            response = packet;
        }
        Assert.True(sawNotification, "moving the stack should notify the connected session");
        var reply = Assert.IsType<PyTuple>(Result(response));
        Assert.Equal(Eve.Services.InventoryWire.Id(stackId), reply.Items[1]);
        var moved = await items.GetAsync(stackId);
        Assert.NotNull(moved);
        Assert.Equal(shipId, moved.LocationId);
        Assert.Equal(5, moved.Flag);

        await rig.RestartAsync();
        moved = await new PgItemStore(rig.Source, rig.InventorySchema).GetAsync(stackId);
        Assert.NotNull(moved);
        Assert.Equal(shipId, moved.LocationId);
        Assert.Equal(5, moved.Flag);
    }
}
