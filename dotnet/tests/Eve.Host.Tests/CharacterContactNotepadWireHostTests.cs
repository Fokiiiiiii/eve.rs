using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Host.Tests;

[Collection("host-composition")]
public sealed class CharacterContactNotepadWireHostTests
{
    [PgFact]
    public async Task Character_personal_mutations_dispatch_through_registered_wire_routes_and_persist()
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        var characterId = await rig.CreateCharacterAsync();
        var contactId = await rig.CreateCharacterAsync();
        var session = new ClientSession { CharacterId = characterId, UserId = 1 };

        var addedContact = await WireCallAsync(rig.Registry, "charMgr", "AddContact", session,
            new PyLong(contactId), new PyInt(-10), PyBool.True, PyBool.False, new PyStr(""));
        Assert.IsType<PyNone>(addedContact);
        var blocked = Enumerable.Range(0, 500).Select(id => (PyValue)new PyLong(90_100_000 + id)).ToArray();
        Assert.IsType<PyNone>(await WireCallAsync(rig.Registry, "charMgr", "BlockOwners", session, new PyList(blocked)));
        Assert.IsType<PyNone>(await WireCallAsync(rig.Registry, "charMgr", "EditContactsRelationshipID", session,
            new PyList([new PyLong(contactId)]), new PyInt(10)));
        Assert.IsType<PyNone>(await WireCallAsync(rig.Registry, "charMgr", "DeleteContacts", session,
            new PyList([new PyLong(contactId)])));

        var noteId = Assert.IsType<PyInt>(await WireCallAsync(rig.Registry, "charMgr", "AddOwnerNote", session,
            new PyStr("N:Planner"), new PyStr("<br>")));
        Assert.True(noteId.Value > 0);
        Assert.IsType<PyNone>(await WireCallAsync(rig.Registry, "charMgr", "EditOwnerNote", session,
            noteId, new PyStr("N:Planner"), new PyStr("wire note")));
        Assert.IsType<PyNone>(await WireCallAsync(rig.Registry, "charMgr", "SaveCharacterSetting", session,
            new PyStr("windowState"), new PyStr("open")));
        Assert.IsType<PyNone>(await WireCallAsync(rig.Registry, "charMgr", "DeleteCharacterSetting", session,
            new PyStr("windowState")));

        await rig.RestartAsync();
        var charMgr = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charMgr"));
        var noteRows = Assert.IsType<PyList>(await WireCallAsync(rig.Registry, "charMgr", "GetOwnerNote", session, noteId));
        Assert.Equal(new PyStr("wire note"), Field(Assert.Single(noteRows.Items), "note"));
        var contacts = Assert.IsType<PyObject>(await WireCallAsync(rig.Registry, "charMgr", "GetContactList", session));
        Assert.Empty(Assert.IsType<PyList>(Field(Assert.IsType<PyObject>(Field(contacts, "addresses")), "lines")).Items);
        Assert.Equal(500, Assert.IsType<PyList>(Field(Assert.IsType<PyObject>(Field(contacts, "blocked")), "lines")).Items.Count);
        Assert.Empty(Assert.IsType<PyDict>(await WireCallAsync(rig.Registry, "charMgr", "GetCharacterSettings", session)).Entries);
        Assert.True(charMgr.HasMethod("AddContact"));
        Assert.True(charMgr.HasMethod("AddOwnerNote"));
        Assert.True(charMgr.HasMethod("BlockOwners"));
        Assert.True(charMgr.HasMethod("DeleteCharacterSetting"));
        Assert.True(charMgr.HasMethod("DeleteContacts"));
        Assert.True(charMgr.HasMethod("EditContactsRelationshipID"));
        Assert.True(charMgr.HasMethod("EditOwnerNote"));
    }

    private sealed class RecordingSink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    private static async Task<PyValue> WireCallAsync(
        ServiceRegistry registry, string service, string method, ClientSession session, params PyValue[] args)
    {
        const long callId = 1;
        var sink = new RecordingSink();
        session.Sink = sink;
        var request = new PyTuple([
            new PyInt(MachoMessage.CallReq),
            new MachoAddress(AddressKind.Client, CallId: new PyLong(callId), ClientId: new PyLong(session.ClientId)).Encode(),
            new MachoAddress(AddressKind.Any, service, new PyLong(callId)).Encode(),
            new PyInt(session.UserId ?? 0),
            new PyTuple([new PyTuple([
                PyNone.Instance,
                new PyStr(method),
                new PyTuple(args),
                new PyDict([new(new PyStr("machoVersion"), new PyInt(3396210))]),
            ])]),
            new PyDict([]),
        ]);

        Assert.True(await new PacketDispatcher(registry).DispatchAsync(request, session));
        var packet = Assert.Single(sink.Packets);
        var response = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(packet).Args);
        var payload = Assert.IsType<PyTuple>(response.Items[4]);
        return Assert.IsType<PySubStream>(payload.Items[0]).Decoded!;
    }

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
