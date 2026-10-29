using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Inventory;
using Eve.Corporations;
using Eve.Skills;
using Eve.Wallet;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Corporation_contact_labels_preserve_optional_fields_and_notify_after_host_mutations()
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        var characterId = await rig.CreateCharacterAsync();
        await rig.FundAsync(characterId, 3_000_000_000m);
        await rig.SetSkillLevelAsync(characterId, 3732, 5);
        var wallets = new PgWalletStore(rig.Source, rig.WalletSchema);
        var store = new CorporationStore(rig.Source, rig.Characters, wallets,
            new WalletTransactions(rig.Source, rig.WalletSchema), new PgSkillStore(rig.Source, rig.SkillsSchema),
            () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), rig.CorporationsSchema);
        var corporation = await store.CreateAsync(characterId,
            new NewCorporation("Contact Labels", "CLBL", "", "", 0), $"host-contact-labels:{Guid.NewGuid():N}");
        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("corpRegistry"));

        Start(configure: registry => registry.Register(service), sessionOpened: session =>
        {
            session.CharacterId = characterId;
            session.Set("charid", new PyLong(characterId));
            session.Set("corpid", new PyLong(corporation.Id));
            session.Set("corporationID", new PyLong(corporation.Id));
            rig.Sessions.Enter(characterId, session);
        });
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        var callId = 1U;
        async Task<PyValue> Call(string method, params PyValue[] args) =>
            await CallOverHost(front, open, callId++, "corpRegistry", method, Py.Tuple(args))
                ?? throw new Xunit.Sdk.XunitException($"{method} returned no reply value");
        async Task AssertContactUpdate(long expectedMask)
        {
            var notice = ReadCorporationNotification(Assert.IsType<Send>(await front.ReceiveAsync()));
            Assert.Equal(("OnOrganizationContactsUpdated", "corpid"), (notice.Name, notice.IdType));
            Assert.Equal(corporation.Id, Number(notice.Args[0]));
            var row = Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyList>(notice.Args[1]).Items));
            Assert.Equal(3, row.Items.Count);
            Assert.Equal(90_300_001, Number(row.Items[0]));
            Assert.Equal(0, Number(row.Items[1]));
            Assert.Equal(expectedMask, Number(row.Items[2]));
        }

        var labelId = Number(await Call("CreateLabel", new PyStr("Operations"), PyNone.Instance));
        var labels = Assert.IsType<PyDict>(await Call("GetLabels"));
        Assert.Same(PyNone.Instance, Field(Assert.IsType<PyObject>(labels.Entries.Single().Value), "color"));
        Assert.Same(PyNone.Instance, await Call("EditLabel", new PyLong(labelId), PyNone.Instance, new PyInt(12)));
        Assert.Same(PyNone.Instance, await Call("EditLabel", new PyLong(labelId), new PyWString("Operations Team"), PyNone.Instance));
        labels = Assert.IsType<PyDict>(await Call("GetLabels"));
        var label = Assert.IsType<PyObject>(labels.Entries.Single().Value);
        Assert.Equal("Operations Team", T(Field(label, "name")));
        Assert.Equal(12, Number(Field(label, "color")));

        var contactId = 90_300_001L;
        Assert.Same(PyNone.Instance, await Call("AddCorporateContact", new PyLong(contactId), new PyInt(0)));
        var addedNotice = ReadCorporationNotification(Assert.IsType<Send>(await front.ReceiveAsync()));
        Assert.Equal(("OnOrganizationContactsUpdated", "corpid"), (addedNotice.Name, addedNotice.IdType));
        Assert.Equal(corporation.Id, Number(addedNotice.Args[0]));
        var addedRow = Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyList>(addedNotice.Args[1]).Items));
        Assert.Equal(contactId, Number(addedRow.Items[0]));
        Assert.Equal(0, Number(addedRow.Items[1]));
        Assert.Same(PyNone.Instance, addedRow.Items[2]);
        Assert.Same(PyNone.Instance, await Call("AssignLabels", new PyList([new PyLong(contactId)]), new PyLong(labelId)));
        await AssertContactUpdate(labelId);
        Assert.Same(PyNone.Instance, await Call("RemoveLabels", new PyList([new PyLong(contactId)]), new PyLong(labelId)));
        await AssertContactUpdate(0);
        Assert.Same(PyNone.Instance, await Call("AssignLabels", new PyList([new PyLong(contactId)]), new PyLong(labelId)));
        await AssertContactUpdate(labelId);
        Assert.Same(PyNone.Instance, await Call("DeleteLabel", new PyLong(labelId)));
        await AssertContactUpdate(0);

        await rig.RestartAsync();
        service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("corpRegistry"));
        var session = new ClientSession { CharacterId = characterId };
        Assert.Empty(Assert.IsType<PyDict>(await service.CallAsync(new ServiceCall
        {
            Method = "GetLabels", Args = [], Session = session,
        })).Entries);
        var contacts = Assert.IsType<PyDict>(await service.CallAsync(new ServiceCall
        {
            Method = "GetCorporateContacts", Args = [], Session = session,
        }));
        Assert.Equal(0, Number(Field(Assert.IsType<PyObject>(contacts.Entries.Single().Value), "labelMask")));
    }

    [PgFact]
    public async Task Alliance_contact_labels_preserve_optional_fields_and_notify_after_host_mutations()
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        var characterId = await rig.CreateCharacterAsync();
        await rig.FundAsync(characterId, 3_000_000_000m);
        await rig.SetSkillLevelAsync(characterId, 3732, 5);
        var wallets = new PgWalletStore(rig.Source, rig.WalletSchema);
        var store = new CorporationStore(rig.Source, rig.Characters, wallets,
            new WalletTransactions(rig.Source, rig.WalletSchema), new PgSkillStore(rig.Source, rig.SkillsSchema),
            () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), rig.CorporationsSchema);
        var corporation = await store.CreateAsync(characterId,
            new NewCorporation("Alliance Labels", "ALBL", "", "", 0), $"host-alliance-labels:{Guid.NewGuid():N}");
        var alliance = await store.CreateAllianceAsync(characterId,
            new NewAlliance("Label Alliance", "LAL", "", ""), $"host-alliance-labels:{Guid.NewGuid():N}");
        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("allianceRegistry"));

        Start(configure: registry => registry.Register(service), sessionOpened: session =>
        {
            session.CharacterId = characterId;
            session.Set("charid", new PyLong(characterId));
            session.Set("corpid", new PyLong(corporation.Id));
            session.Set("corporationID", new PyLong(corporation.Id));
            session.Set("allianceid", new PyLong(alliance.Id));
            session.Set("allianceID", new PyLong(alliance.Id));
            rig.Sessions.Enter(characterId, session);
        });
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        var callId = 1U;
        async Task<PyValue> Call(string method, params PyValue[] args) =>
            await CallOverHostAfterQueuedNotifications(front, open, callId++, "allianceRegistry", method, Py.Tuple(args))
                ?? throw new Xunit.Sdk.XunitException($"{method} returned no reply value");
        async Task AssertContactUpdate(long expectedMask)
        {
            var notice = ReadCorporationNotification(Assert.IsType<Send>(await front.ReceiveAsync()));
            Assert.Equal(("OnOrganizationContactsUpdated", "allianceid"), (notice.Name, notice.IdType));
            Assert.Equal(alliance.Id, Number(notice.Args[0]));
            var row = Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyList>(notice.Args[1]).Items));
            Assert.Equal(3, row.Items.Count);
            Assert.Equal(90_400_001, Number(row.Items[0]));
            Assert.Equal(0, Number(row.Items[1]));
            Assert.Equal(expectedMask, Number(row.Items[2]));
        }

        var labelId = Number(await Call("CreateLabel", new PyStr("Operations"), PyNone.Instance));
        var labels = Assert.IsType<PyDict>(await Call("GetLabels"));
        Assert.Same(PyNone.Instance, Field(Assert.IsType<PyObject>(labels.Entries.Single().Value), "color"));
        Assert.Same(PyNone.Instance, await Call("EditLabel", new PyLong(labelId), PyNone.Instance, new PyInt(12)));
        Assert.Same(PyNone.Instance, await Call("EditLabel", new PyLong(labelId), new PyStr("Operations Team"), PyNone.Instance));
        labels = Assert.IsType<PyDict>(await Call("GetLabels"));
        var label = Assert.IsType<PyObject>(labels.Entries.Single().Value);
        Assert.Equal("Operations Team", T(Field(label, "name")));
        Assert.Equal(12, Number(Field(label, "color")));

        var contactId = 90_400_001L;
        Assert.Same(PyNone.Instance, await Call("AddAllianceContact", new PyLong(contactId), new PyInt(0)));
        _ = ReadCorporationNotification(Assert.IsType<Send>(await front.ReceiveAsync()));
        var labelMask = 1L << ((int)labelId - 1);
        Assert.Same(PyNone.Instance, await Call("AssignLabels", new PyList([new PyLong(contactId)]), new PyLong(labelMask)));
        await AssertContactUpdate(labelMask);
        Assert.Same(PyNone.Instance, await Call("RemoveLabels", new PyList([new PyLong(contactId)]), new PyLong(labelMask)));
        await AssertContactUpdate(0);
        Assert.Same(PyNone.Instance, await Call("AssignLabels", new PyList([new PyLong(contactId)]), new PyLong(labelMask)));
        await AssertContactUpdate(labelMask);
        Assert.Same(PyNone.Instance, await Call("DeleteLabel", new PyLong(labelId)));
        await AssertContactUpdate(0);

        await rig.RestartAsync();
        service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("allianceRegistry"));
        var session = new ClientSession { CharacterId = characterId };
        Assert.Empty(Assert.IsType<PyDict>(await service.CallAsync(new ServiceCall
        {
            Method = "GetLabels", Args = [], Session = session,
        })).Entries);
        var contacts = Assert.IsType<PyDict>(await service.CallAsync(new ServiceCall
        {
            Method = "GetAllianceContacts", Args = [], Session = session,
        }));
        Assert.Equal(0, Number(Field(Assert.IsType<PyObject>(contacts.Entries.Single().Value), "labelMask")));
    }

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

        var folderLabels = Assert.IsType<PyObject>(await CallOverHost(front, open, 8, "charMgr", "GetOwnerNoteLabels", Py.Tuple([])));
        var virtualFolder = Assert.IsType<PyList>(Assert.Single(Assert.IsType<PyList>(PersonalWireField(folderLabels, "lines")).Items));
        var folderId = virtualFolder.Items[0];
        Assert.Equal("S:Folders", T(virtualFolder.Items[1]));
        Assert.Equal(PyNone.Instance, await CallOverHost(front, open, 9, "charMgr", "EditOwnerNote",
            Py.Tuple(folderId, new PyStr("S:Folders"), new PyStr("2::F::0::Projects|"))));
        var folderRows = Assert.IsType<PyList>(await CallOverHost(front, open, 10, "charMgr", "GetOwnerNote", Py.Tuple(folderId)));
        Assert.Equal("2::F::0::Projects|", T(PersonalWireField(Assert.Single(folderRows.Items), "note")));

        var noteId = Assert.IsType<PyInt>(await CallOverHost(front, open, 11, "charMgr", "AddOwnerNote",
            Py.Tuple(new PyStr("N:Planner"), new PyStr("<br>"))));
        Assert.True(noteId.Value > 0);
        Assert.Equal(PyNone.Instance, await CallOverHost(front, open, 12, "charMgr", "EditOwnerNote",
            Py.Tuple(noteId, new PyStr("N:Planner renamed"))));
        Assert.Equal(PyNone.Instance, await CallOverHost(front, open, 13, "charMgr", "EditOwnerNote",
            Py.Tuple(noteId, new PyStr("N:Planner"), new PyStr("client note"))));
        var noteRows = Assert.IsType<PyList>(await CallOverHost(front, open, 14, "charMgr", "GetOwnerNote", Py.Tuple(noteId)));
        Assert.Equal("N:Planner", T(PersonalWireField(Assert.Single(noteRows.Items), "label")));
        var note = PersonalWireField(Assert.Single(noteRows.Items), "note");
        Assert.IsType<PyBytes>(note);
        Assert.Equal("client note", T(note));

        Assert.Equal(PyNone.Instance, await CallOverHostAfterQueuedNotifications(front, open, 15, "charMgr", "SaveCharacterSetting",
            Py.Tuple(new PyStr("windowState"), new PyStr("open"))));
        Assert.Equal(PyNone.Instance, await CallOverHostAfterQueuedNotifications(front, open, 16, "charMgr", "DeleteCharacterSetting",
            Py.Tuple(new PyStr("windowState"))));
        Assert.Empty(Assert.IsType<PyDict>(await CallOverHostAfterQueuedNotifications(front, open, 17, "charMgr", "GetCharacterSettings",
            Py.Tuple([]))).Entries);

        await rig.RestartAsync();
        service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charMgr"));
        var persisted = Assert.IsType<PyList>(await service.CallAsync(new ServiceCall
        {
            Method = "GetOwnerNote", Args = [noteId], Session = new ClientSession { CharacterId = characterId },
        }));
        Assert.Equal("client note", T(PersonalWireField(Assert.Single(persisted.Items), "note")));
        var persistedFolders = Assert.IsType<PyList>(await service.CallAsync(new ServiceCall
        {
            Method = "GetOwnerNote", Args = [folderId], Session = new ClientSession { CharacterId = characterId },
        }));
        Assert.Equal("S:Folders", T(PersonalWireField(Assert.Single(persistedFolders.Items), "label")));
        Assert.Equal("2::F::0::Projects|", T(PersonalWireField(Assert.Single(persistedFolders.Items), "note")));
    }

    private async Task<PyValue?> CallOverHostAfterQueuedNotifications(
        TestFront front, SessionOpen open, uint callId, string service, string method, PyValue args)
    {
        await front.SendAsync(new Packet(open.SessionId,
            MarshalCodec.Encode(Packets.CallWithKwargs(open, callId, service, method, args, ClientKwargs()))));
        while (true)
        {
            var send = Assert.IsType<Send>(await front.ReceiveAsync());
            var decoded = MarshalCodec.Decode(send.Payload);
            var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded));
            if (packet.Type == MachoMessage.Notification)
                continue;
            Assert.Equal(MachoMessage.CallRsp, packet.Type);
            return Result(decoded);
        }
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
