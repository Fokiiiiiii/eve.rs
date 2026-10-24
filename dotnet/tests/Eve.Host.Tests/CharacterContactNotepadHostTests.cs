using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Inventory;

namespace Eve.Host.Tests;

[Collection("host-composition")]
public sealed class CharacterContactNotepadHostTests
{
    [PgFact]
    public async Task Character_contact_note_and_setting_rpcs_round_trip_through_registered_host()
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        var characterId = await rig.CreateCharacterAsync();
        var contactId = await rig.CreateCharacterAsync();
        var session = new ClientSession { CharacterId = characterId, UserId = 1 };
        var charMgr = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charMgr"));

        Assert.True(charMgr.HasMethod("AddContact"));
        Assert.True(charMgr.HasMethod("AddOwnerNote"));
        Assert.True(charMgr.HasMethod("BlockOwners"));
        Assert.True(charMgr.HasMethod("DeleteCharacterSetting"));
        Assert.True(charMgr.HasMethod("DeleteContacts"));
        Assert.True(charMgr.HasMethod("EditContactsRelationshipID"));
        Assert.True(charMgr.HasMethod("EditOwnerNote"));

        Assert.Same(PyNone.Instance, await CallAsync(charMgr, "AddContact", session, new PyLong(contactId)));
        Assert.Same(PyNone.Instance, await CallAsync(charMgr, "AddContact", session,
            new PyLong(contactId), new PyInt(-10), PyBool.True, PyBool.False, new PyStr("")));
        var contacts = Assert.IsType<PyObject>(await CallAsync(charMgr, "GetContactList", session));
        var addresses = Assert.IsType<PyObject>(Field(contacts, "addresses"));
        Assert.Equal(new PyList([new PyList([
            new PyInt(contactId), new PyInt(1), new PyInt(-10), new PyInt(0)])]),
            Field(addresses, "lines"));

        var blockedOwners = Enumerable.Range(0, 500).Select(id => (PyValue)new PyLong(90_100_000 + id)).ToArray();
        Assert.Same(PyNone.Instance, await CallAsync(charMgr, "BlockOwners", session, new PyList(blockedOwners)));
        Assert.IsType<PyNone>(await CallAsync(charMgr, "EditContactsRelationshipID", session,
            new PyList([new PyLong(contactId)]), new PyInt(10)));
        var changedContacts = Assert.IsType<PyObject>(await CallAsync(charMgr, "GetContactList", session));
        var changedAddresses = Assert.IsType<PyObject>(Field(changedContacts, "addresses"));
        Assert.Equal(new PyList([new PyList([
            new PyInt(contactId), new PyInt(1), new PyInt(10), new PyInt(0)])]),
            Field(changedAddresses, "lines"));
        var deleteIds = Enumerable.Range(1, 500).Select(id => (PyValue)new PyLong(90_200_000 + id)).ToArray();
        deleteIds[0] = new PyLong(contactId);
        Assert.Same(PyNone.Instance, await CallAsync(charMgr, "DeleteContacts", session, new PyList(deleteIds)));

        var noteId = Assert.IsType<PyInt>(await CallAsync(charMgr, "AddOwnerNote", session,
            new PyStr("N:Planner"), new PyStr("<br>")));
        Assert.True(noteId.Value > 0);
        Assert.Same(PyNone.Instance, await CallAsync(charMgr, "EditOwnerNote", session,
            noteId, new PyStr("N:Planner renamed")));
        Assert.Same(PyNone.Instance, await CallAsync(charMgr, "EditOwnerNote", session,
            noteId, new PyStr("N:Planner"), new PyStr("client note")));
        var noteRows = Assert.IsType<PyList>(await CallAsync(charMgr, "GetOwnerNote", session, noteId));
        var note = Assert.Single(noteRows.Items);
        Assert.Equal(new PyStr("N:Planner"), Field(note, "label"));
        Assert.Equal(new PyStr("client note"), Field(note, "note"));

        Assert.Same(PyNone.Instance, await CallAsync(charMgr, "SaveCharacterSetting", session,
            new PyStr("windowState"), new PyStr("open")));
        Assert.Same(PyNone.Instance, await CallAsync(charMgr, "DeleteCharacterSetting", session,
            new PyStr("windowState")));

        await rig.RestartAsync();
        charMgr = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charMgr"));
        var persistedContacts = Assert.IsType<PyObject>(await CallAsync(charMgr, "GetContactList", session));
        Assert.Empty(Assert.IsType<PyList>(Field(Assert.IsType<PyObject>(Field(persistedContacts, "addresses")), "lines")).Items);
        Assert.Equal(500, Assert.IsType<PyList>(Field(Assert.IsType<PyObject>(Field(persistedContacts, "blocked")), "lines")).Items.Count);
        var persistedNoteRows = Assert.IsType<PyList>(await CallAsync(charMgr, "GetOwnerNote", session, noteId));
        Assert.Equal(new PyStr("client note"), Field(Assert.Single(persistedNoteRows.Items), "note"));
        Assert.Empty(Assert.IsType<PyDict>(await CallAsync(charMgr, "GetCharacterSettings", session)).Entries);
    }

    [PgFact]
    public async Task Bound_inventory_add_accepts_client_quantity_and_transport_kwargs_and_moves_the_item()
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
                new NewItem(587, characterId, rig.Station, 4));
            stackId = await items.CreateAsync(connection, transaction,
                new NewItem(34, characterId, rig.Station, 4, 10, Singleton: false));
            await transaction.CommitAsync();
        }

        var session = new ClientSession { CharacterId = characterId, UserId = 1 };
        session.Set("stationid", new PyLong(rig.Station));
        var invBroker = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("invbroker"));
        Assert.True(invBroker.HasMethod("MachoBindObject"));
        var boundCall = await invBroker.CallAsync(new ServiceCall
        {
            Method = "MachoBindObject",
            Session = session,
            Args =
            [
                new PyTuple([new PyLong(shipId)]),
                new PyTuple([
                    new PyStr("Add"),
                    new PyTuple([new PyLong(stackId), new PyLong(rig.Station)]),
                    new PyDict([
                        new(new PyStr("qty"), PyNone.Instance),
                        new(new PyStr("flag"), new PyInt(5)),
                        new(new PyStr("machoVersion"), new PyInt(3396210)),
                    ]),
                ]),
            ],
        });

        var binding = Assert.IsType<PyTuple>(boundCall);
        Assert.Same(PyNone.Instance, binding.Items[1]);
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

    private static async Task<PyValue> CallAsync(IService service, string method, ClientSession session, params PyValue[] args) =>
        await service.CallAsync(new ServiceCall { Method = method, Args = args, Session = session });

    private static long IntegerValue(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => throw new Xunit.Sdk.XunitException($"Expected an integer, got {value.GetType().Name}"),
    };

    private static PyValue Field(PyValue value, string name)
    {
        var entries = value switch
        {
            PyObject obj => Assert.IsType<PyDict>(obj.Args).Entries,
            PyDict dict => dict.Entries,
            _ => throw new Xunit.Sdk.XunitException($"Expected a mapping, got {value.GetType().Name}"),
        };
        return entries.Single(entry => Assert.IsType<PyStr>(entry.Key).Value == name).Value;
    }
}
