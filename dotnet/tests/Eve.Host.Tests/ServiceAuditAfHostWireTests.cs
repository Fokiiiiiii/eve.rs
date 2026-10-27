using Eve.Characters;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.CharacterInfo;
using Eve.Universe;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Char_mgr_client_rows_keep_their_registered_host_shapes()
    {
        const long characterId = 90_000_101;
        const long contactId = 90_000_102;
        const long noteId = 90_000_103;
        const long itemId = 90_000_104;
        var character = new Character(
            characterId, 1, "Wire Pilot", 1, 1, 1, 1, 35, 1373, 1_000_001,
            null, null, 60_000_001, 60_000_001, 30_000_142, 20_000_020, 10_000_002, null,
            DateTimeOffset.UnixEpoch, null, null);
        var personal = CharMgrPersonalData.Empty with
        {
            Contacts = [new CharMgrContact(contactId, false, -10, 7)],
            BlockedSenders = [90_000_105],
            OwnerNotes = [new CharMgrOwnerNote(noteId, "N:Planner", "owner note")],
            ItemNotes = new Dictionary<long, string> { [itemId] = "character note" },
            CharacterSettings = new Dictionary<string, PyValue>
            {
                ["windowState"] = new PyStr("open"),
            },
        };
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
            readCharacterById: (id, _) =>
                ValueTask.FromResult<Character?>(id == characterId ? character : null));

        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.CharacterId = characterId);
        using var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var settings = Assert.IsType<PyDict>(await CallOverHost(
            front, open, 1, "charMgr", "GetCharacterSettings", Py.Tuple([])));
        Assert.Equal("open", T(settings.Entries.Single(entry => T(entry.Key) == "windowState").Value));

        Assert.Empty(Assert.IsType<PyList>(await CallOverHost(
            front, open, 2, "charMgr", "GetCohortsForCharacter", Py.Tuple([]))).Items);

        var contacts = Assert.IsType<PyObject>(await CallOverHost(
            front, open, 3, "charMgr", "GetContactList", Py.Tuple([])));
        var addresses = PersonalWireField(contacts, "addresses");
        Assert.Equal(["contactID", "inWatchlist", "relationshipID", "labelMask"],
            Assert.IsType<PyList>(PersonalWireField(addresses, "header")).Items.Select(T));
        Assert.Equal(new PyToken("util.Row"), PersonalWireField(addresses, "RowClass"));
        Assert.Equal(new PyList([new PyList([
            new PyInt(contactId), new PyInt(0), new PyInt(-10), new PyInt(7)])]),
            PersonalWireField(addresses, "lines"));
        var blocked = PersonalWireField(contacts, "blocked");
        Assert.Equal(["senderID"],
            Assert.IsType<PyList>(PersonalWireField(blocked, "header")).Items.Select(T));
        Assert.Equal(new PyList([new PyList([new PyInt(90_000_105)])]),
            PersonalWireField(blocked, "lines"));

        Assert.Same(PyNone.Instance, await CallOverHost(
            front, open, 4, "charMgr", "EditContactsRelationshipID",
            Py.Tuple(new PyList([new PyLong(contactId)]), new PyInt(5))));
        contacts = Assert.IsType<PyObject>(await CallOverHost(
            front, open, 5, "charMgr", "GetContactList", Py.Tuple([])));
        addresses = PersonalWireField(contacts, "addresses");
        Assert.Equal(new PyList([new PyList([
            new PyInt(contactId), new PyInt(0), new PyInt(5), new PyInt(7)])]),
            PersonalWireField(addresses, "lines"));

        Assert.Same(PyNone.Instance, await CallOverHost(
            front, open, 6, "charMgr", "EditOwnerNote",
            Py.Tuple(new PyLong(noteId), new PyStr("N:Planner renamed"))));
        Assert.Same(PyNone.Instance, await CallOverHost(
            front, open, 7, "charMgr", "EditOwnerNote",
            Py.Tuple(new PyLong(noteId), new PyStr("N:Planner"), new PyStr("updated note"))));
        var notes = Assert.IsType<PyList>(await CallOverHost(
            front, open, 8, "charMgr", "GetOwnerNote", Py.Tuple(new PyLong(noteId))));
        var note = Assert.Single(notes.Items);
        Assert.Equal("util.KeyVal", T(Assert.IsType<PyObject>(note).Name));
        Assert.Equal("N:Planner", T(PersonalWireField(note, "label")));
        Assert.Equal("updated note", T(PersonalWireField(note, "note")));

        Assert.Equal("character note", T(await CallOverHost(
            front, open, 9, "charMgr", "GetNote", Py.Tuple(new PyLong(itemId)))));

        Assert.Same(PyNone.Instance, await CallOverHost(
            front, open, 10, "charMgr", "DeleteContacts",
            Py.Tuple(new PyList([new PyLong(contactId)]))));
        contacts = Assert.IsType<PyObject>(await CallOverHost(
            front, open, 11, "charMgr", "GetContactList", Py.Tuple([])));
        Assert.Empty(Assert.IsType<PyList>(
            PersonalWireField(PersonalWireField(contacts, "addresses"), "lines")).Items);

        Assert.Empty(Assert.IsType<PyDict>(await CallOverHost(
            front, open, 12, "charMgr", "GetOrganizationInfoForCharacters",
            Py.Tuple(new PyList([])))).Entries);
    }

    [Fact]
    public async Task Char_mgr_home_station_row_exposes_the_station_id_the_client_reads()
    {
        const long structureId = 88_000_101;
        const long characterId = 90_000_111;
        const long corporationId = 98_000_101;
        const int systemId = 30_000_142;
        var character = new Character(
            characterId, 1, "Home Pilot", 1, 1, 1, 10, 35, 1373,
            corporationId, null, null, structureId, structureId,
            systemId, 20_010_001, 10_001_004, null, DateTimeOffset.UnixEpoch, null, null);
        var resolver = new HomeStationResolver(
            (id, _) => Task.FromResult<Character?>(id == characterId ? character : null),
            new StationCatalog([]),
            new CreationCatalog([], [], []),
            (id, _) => Task.FromResult<HomeStationStructure?>(id == structureId
                ? new HomeStationStructure(
                    new HomeStationLocation(structureId, 35_841, "Host Structure Home", systemId, 20_010_001,
                        10_001_004, corporationId, false),
                    PersistentlyDeployed: true,
                    IsLost: false)
                : null));
        var service = new CharMgrService(null!, resolver, null!, null!, null!, null!, null!);

        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.CharacterId = characterId);
        using var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var row = Assert.IsType<PyObject>(await CallOverHost(
            front, open, 1, "charMgr", "GetHomeStationRow", Py.Tuple([])));
        Assert.Equal("util.KeyVal", T(row.Name));
        Assert.Equal(new PyInt(structureId), PersonalWireField(row, "stationID"));
        Assert.Equal(new PyInt(systemId), PersonalWireField(row, "solarSystemID"));
        Assert.Equal(new PyInt(35_841), PersonalWireField(row, "stationTypeID"));
        Assert.Equal("Host Structure Home", T(PersonalWireField(row, "stationName")));
    }
}
