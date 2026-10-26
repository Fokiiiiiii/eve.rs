using Eve.Characters;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.CharacterInfo;
using Eve.Services.CharacterSelect;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Character_creation_date_uses_the_session_character_when_called_without_arguments()
    {
        const long characterId = 90_000_001;
        var character = new Character(
            characterId, 1, "Wire Pilot", 1, 1, 1, 1, 35, 1373, 1_000_001,
            null, null, 60_000_001, 60_000_001, 30_000_142, 20_000_020, 10_000_002, null,
            DateTimeOffset.UnixEpoch, null, null);
        var service = new CharMgrService(
            characters: null!,
            homeStations: null!,
            readRecentShipKillmailPage: (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<PyValue>>([]),
            readCharacterDescription: (_, _) => ValueTask.FromResult<string?>(null),
            writeCharacterDescription: (_, _, _) => ValueTask.CompletedTask,
            readCharacterSecurityStatus: (_, _) => ValueTask.FromResult(0d),
            readMedal1GraphicId: (_, _) => ValueTask.FromResult<long?>(null),
            readCharacterById: (id, _) => ValueTask.FromResult<Character?>(id == characterId ? character : null));

        Start(configure: registry => registry.Register(service),
            sessionOpened: session => session.CharacterId = characterId);
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        Assert.Equal(new PyLong(116_444_736_000_000_000),
            await CallOverHost(front, open, 1, "charMgr", "GetCharacterCreationDate", Py.Tuple([])));
    }

    [Fact]
    public async Task Contact_and_owner_note_mutations_return_the_values_the_client_uses()
    {
        const long characterId = 90_000_001;
        const long contactId = 90_000_002;
        var personal = CharMgrPersonalData.Empty;
        var contactCharacter = new Character(
            contactId, 1, "Contact Pilot", 1, 1, 1, 1, 35, 1373, 1_000_001,
            null, null, 60_000_001, 60_000_001, 30_000_142, 20_000_020, 10_000_002, null,
            DateTimeOffset.UnixEpoch, null, null);
        (long Sender, long Receiver, int Type, long Relationship, string Message)? notification = null;
        var service = new CharMgrService(
            characters: null!,
            homeStations: null!,
            readRecentShipKillmailPage: (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<PyValue>>([]),
            readCharacterDescription: (_, _) => ValueTask.FromResult<string?>(null),
            writeCharacterDescription: (_, _, _) => ValueTask.CompletedTask,
            readCharacterSecurityStatus: (_, _) => ValueTask.FromResult(0d),
            readMedal1GraphicId: (_, _) => ValueTask.FromResult<long?>(null),
            readPersonalData: (_, _) => ValueTask.FromResult<CharMgrPersonalData?>(personal),
            updatePersonalData: (_, update, _) =>
            {
                personal = update(personal);
                return ValueTask.FromResult<CharMgrPersonalData?>(personal);
            },
            readCharacterById: (id, _) => ValueTask.FromResult<Character?>(id == contactId ? contactCharacter : null),
            writeContactNotification: (sender, receiver, type, relationship, message, _) =>
            {
                notification = (sender, receiver, type, relationship, message);
                return ValueTask.CompletedTask;
            });

        Start(configure: registry => registry.Register(service),
            sessionOpened: session => session.CharacterId = characterId);
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        Assert.Equal(PyNone.Instance, await CallOverHost(front, open, 1, "charMgr", "AddContact",
            Py.Tuple(new PyLong(contactId), new PyInt(-10), PyBool.True, PyBool.True, new PyStr("Please add me"))));
        Assert.Equal((characterId, contactId, 89, -10L, "Please add me"), notification);
        var contacts = Assert.IsType<PyObject>(
            await CallOverHost(front, open, 2, "charMgr", "GetContactList", Py.Tuple([])));
        Assert.Equal(new PyList([new PyList([
            new PyInt(contactId), new PyInt(1), new PyInt(-10), new PyInt(0)])]),
            PersonalWireField(PersonalWireField(contacts, "addresses"), "lines"));

        Assert.Equal(PyNone.Instance, await CallOverHost(front, open, 3, "charMgr", "DeleteContacts",
            Py.Tuple(new PyList([new PyLong(contactId)]))));
        contacts = Assert.IsType<PyObject>(
            await CallOverHost(front, open, 4, "charMgr", "GetContactList", Py.Tuple([])));
        Assert.Empty(Assert.IsType<PyList>(
            PersonalWireField(PersonalWireField(contacts, "addresses"), "lines")).Items);

        var noteId = Assert.IsType<PyInt>(await CallOverHost(front, open, 5, "charMgr", "AddOwnerNote",
            Py.Tuple(new PyStr("N:Planner"), new PyStr("<br>"))));
        Assert.True(noteId.Value > 0);
        var notes = Assert.IsType<PyList>(
            await CallOverHost(front, open, 6, "charMgr", "GetOwnerNote", Py.Tuple(noteId)));
        Assert.Equal("N:Planner", T(PersonalWireField(Assert.Single(notes.Items), "label")));
    }

    [Fact]
    public async Task Get_num_characters_returns_an_integer_without_an_account_bound_to_the_session()
    {
        var catalog = new CreationCatalog([], [], []);
        var service = new CharUnboundMgrService(
            null!, null!, null!, null!, null!, null!, catalog, null!, [], new Random(1));
        Start(configure: registry => registry.Register(service),
            sessionOpened: session => session.UserId = null);
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        Assert.Equal(new PyInt(0),
            await CallOverHost(front, open, 1, "charUnboundMgr", "GetNumCharacters", Py.Tuple([])));
    }
}
