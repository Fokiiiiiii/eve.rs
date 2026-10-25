using Eve.Marshal;
using Eve.Macho;
using Eve.Services;

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
        Assert.Equal(new PyStr("N:Planner"), PersonalWireField(Assert.Single(noteRows.Items), "label"));
        Assert.Equal(new PyStr("client note"), PersonalWireField(Assert.Single(noteRows.Items), "note"));

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
}
