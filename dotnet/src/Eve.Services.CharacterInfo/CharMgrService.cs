using Eve.Characters;
using Eve.Marshal;
using Eve.Universe;

namespace Eve.Services.CharacterInfo;

/// <summary>
/// What the client asks about the character it plays and about other characters (<c>charMgr</c>): the public record, the
/// home station, the creation date, the contact list. Notes, contacts and settings that a player edits come with the
/// modules that keep them; until then those answer as for a player with none.
/// </summary>
public sealed record CharMgrContact(long ContactId, bool InWatchlist, long RelationshipId, long LabelMask);

public sealed record CharMgrContactLabel(string Name, long Color);

public sealed record CharMgrOwnerNote(long NoteId, string Label, string Note);

public sealed record CharMgrPersonalData(
    IReadOnlyList<CharMgrContact> Contacts,
    IReadOnlyList<long> BlockedSenders,
    IReadOnlyDictionary<long, CharMgrContactLabel> Labels,
    IReadOnlyList<CharMgrOwnerNote> OwnerNotes,
    IReadOnlyDictionary<long, string> ItemNotes)
{
    public static CharMgrPersonalData Empty { get; } = new(
        Array.Empty<CharMgrContact>(),
        Array.Empty<long>(),
        new Dictionary<long, CharMgrContactLabel>(),
        Array.Empty<CharMgrOwnerNote>(),
        new Dictionary<long, string>());

    public long? NextOwnerNoteId { get; init; }
}
[Service("charMgr")]
public sealed partial class CharMgrService(
    PgCharacterStore characters,
    StationCatalog stations,
    Func<long, CancellationToken, ValueTask<CharMgrPersonalData?>>? readPersonalData = null)
{
    /// <summary>The reference's default text for the character description is its own branding; a new character has none.</summary>
    private const string NoDescription = "";

    /// <summary>
    /// The empire a corporation's members are shown under. The reference names four of the starting corporations and
    /// falls back to the corporation's faction for the others.
    /// </summary>
    private static readonly IReadOnlyDictionary<long, long> EmpireOfCorporation = new Dictionary<long, long>
    {
        [1000044] = 500001,
        [1000115] = 500002,
        [1000009] = 500003,
        [1000006] = 500004,
    };

    /// <summary>What the client checks with its own signature to run: the reference sends this, unsigned (signature 0).</summary>
    private static readonly byte[] SettingsCode = Convert.FromHexString(
        "630000000000000000010000004300000073040000006900005328010000004e280000000028000000002800000000280000000073080000003c737472696e673e740100000066010000007300000000");

    private static PyValue None => PyNone.Instance;

    public static long EmpireOf(long corporationId, long? factionId) =>
        EmpireOfCorporation.TryGetValue(corporationId, out var empire) ? empire : factionId ?? 0;

    private static long? IdOf(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => null,
    };

    private static PyValue FileTime(DateTimeOffset time) => new PyLong(Py.FileTimeFromUnixMs(time.ToUnixTimeMilliseconds()));

    private static PyValue Nullable(long? value) => value is { } id ? new PyInt(id) : None;

    /// <summary>The public record of a character: <c>util.KeyVal</c> of the fields the client's character windows read.</summary>
    internal static PyValue PublicInfo(Character c)
    {
        var empire = EmpireOf(c.CorporationId, c.FactionId);
        return Py.KeyVal(
            ("characterID", new PyInt(c.Id)),
            ("characterName", new PyStr(c.Name)),
            ("typeID", new PyInt(c.TypeId)),
            ("raceID", new PyInt(c.RaceId)),
            ("bloodlineID", new PyInt(c.BloodlineId)),
            ("ancestryID", new PyInt(c.AncestryId)),
            ("corporationID", new PyInt(c.CorporationId)),
            ("allianceID", Nullable(c.AllianceId)),
            ("factionID", Nullable(c.FactionId)),
            ("empireID", empire == 0 ? None : new PyInt(empire)),
            ("schoolID", new PyInt(c.SchoolId)),
            ("gender", new PyInt(c.Gender)),
            ("createDateTime", FileTime(c.CreatedAt)),
            ("startDateTime", FileTime(c.CreatedAt)),
            ("description", new PyStr(NoDescription)),
            ("securityRating", new PyInt(0)),
            ("securityStatus", new PyInt(0)),
            ("bounty", new PyInt(0)),
            ("title", new PyStr("")),
            ("shortName", new PyStr("none")),
            ("stationID", new PyInt(c.StationId)),
            ("solarSystemID", new PyInt(c.SolarSystemId)),
            ("militiaFactionID", None),
            ("medal1GraphicID", None));
    }

    private async ValueTask<Character?> CharacterAsync(ServiceCall call)
    {
        var id = IdOf(call.Arg(0));
        return id is null ? null : await characters.GetAsync(id.Value, call.Cancellation);
    }

    [Method("GetPublicInfo")]
    public async ValueTask<PyValue> GetPublicInfo(ServiceCall call) =>
        await CharacterAsync(call) is { } character ? PublicInfo(character) : None;

    /// <summary>The same record in a list (the client asks for it with a second argument for the character window).</summary>
    [Method("GetPublicInfo3")]
    public async ValueTask<PyValue> GetPublicInfo3(ServiceCall call) =>
        await CharacterAsync(call) is { } character ? new PyList([PublicInfo(character)]) : None;

    [Method("GetCharacterDescription")]
    public async ValueTask<PyValue> GetCharacterDescription(ServiceCall call) =>
        await CharacterAsync(call) is not null ? new PyStr(NoDescription) : None;

    [Method("GetCharacterCreationDate")]
    public async ValueTask<PyValue> GetCharacterCreationDate(ServiceCall call) =>
        await CharacterAsync(call) is { } character ? FileTime(character.CreatedAt) : None;

    /// <summary>The station the character's clone is at, in every spelling the client has asked for it in.</summary>
    [Method("GetHomeStationRow")]
    public async ValueTask<PyValue> GetHomeStationRow(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } id || await characters.GetAsync(id, call.Cancellation) is not { } character
            || stations.Get(character.HomeStationId) is not { } station)
        {
            return None;
        }
        PyValue Id(long value) => new PyInt(value);
        PyValue Text(string value) => new PyStr(value);
        return Py.KeyVal(
            ("id", Id(station.Id)), ("station_id", Id(station.Id)), ("stationID", Id(station.Id)), ("home_station_id", Id(station.Id)),
            ("type_id", Id(station.TypeId)), ("typeID", Id(station.TypeId)), ("station_type_id", Id(station.TypeId)),
            ("name", Text(station.Name)), ("station_name", Text(station.Name)), ("stationName", Text(station.Name)),
            ("solar_system_id", Id(station.SolarSystemId)), ("solarSystemID", Id(station.SolarSystemId)),
            ("constellation_id", Id(station.ConstellationId)), ("constellationID", Id(station.ConstellationId)),
            ("region_id", Id(station.RegionId)), ("regionID", Id(station.RegionId)),
            ("owner_id", Id(station.OwnerId)), ("ownerID", Id(station.OwnerId)),
            ("clone_station_id", Id(station.Id)), ("cloneStationID", Id(station.Id)),
            ("is_fallback", PyBool.False), ("isFallback", PyBool.False),
            ("stationTypeID", Id(station.TypeId)));
    }

    [Method("GetPaperdollState")]
    public PyValue GetPaperdollState(ServiceCall call) => new PyInt(0);

    [Method("GetCohortsForCharacter")]
    public PyValue GetCohortsForCharacter(ServiceCall call) => new PyList([]);

    [Method("GetSettingsInfo")]
    public PyValue GetSettingsInfo(ServiceCall call) => Py.Tuple(new PyBuffer(SettingsCode), new PyInt(0));

    /// <summary>The contacts and the blocked senders: none until the contacts module exists.</summary>
    private async ValueTask<CharMgrPersonalData> ReadPersonalDataAsync(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } characterId || characterId <= 0 || readPersonalData is null)
        {
            return CharMgrPersonalData.Empty;
        }

        return await readPersonalData(characterId, call.Cancellation) ?? CharMgrPersonalData.Empty;
    }

    [Method("GetContactList")]

    public async ValueTask<PyValue> GetContactList(ServiceCall call)
    {
        var data = await ReadPersonalDataAsync(call);
        var addresses = data.Contacts
            .Where(contact => contact.ContactId > 0)
            .OrderBy(contact => contact.ContactId)
            .Select(contact => (PyValue)new PyList([
                new PyInt(contact.ContactId),
                new PyInt(contact.InWatchlist ? 1 : 0),
                new PyInt(contact.RelationshipId),
                new PyInt(contact.LabelMask),
            ]))
            .ToArray();
        var blocked = data.BlockedSenders
            .Where(senderId => senderId > 0)
            .Distinct()
            .Order()
            .Select(senderId => (PyValue)new PyList([new PyInt(senderId)]))
            .ToArray();

        return Py.KeyVal(
            ("addresses", Py.Rowset(["contactID", "inWatchlist", "relationshipID", "labelMask"], addresses)),
            ("blocked", Py.Rowset(["senderID"], blocked)));
    }



    [Method("GetLabels")]

    public async ValueTask<PyValue> GetLabels(ServiceCall call)
    {
        var data = await ReadPersonalDataAsync(call);
        var entries = data.Labels
            .Where(label => label.Key > 0)
            .OrderBy(label => label.Key)
            .Select(label => new KeyValuePair<PyValue, PyValue>(
                new PyInt(label.Key),
                Py.KeyVal(
                    ("labelID", new PyInt(label.Key)),
                    ("name", new PyStr(label.Value.Name)),
                    ("color", new PyInt(label.Value.Color)))))
            .ToArray();
        return new PyDict(entries);
    }



    /// <summary>The reference exposes its default notes folder for a bound character.</summary>
    private static CharMgrOwnerNote? DefaultFoldersNote(CharMgrPersonalData data)
    {
        if (data.OwnerNotes.Any(note => note.Label == "S:Folders"))
        {
            return null;
        }

        var usedIds = data.OwnerNotes.Select(note => note.NoteId).ToHashSet();
        var noteId = data.NextOwnerNoteId is > 0 ? data.NextOwnerNoteId.Value : 1;
        while (usedIds.Contains(noteId) && noteId < long.MaxValue)
        {
            noteId++;
        }

        return usedIds.Contains(noteId)
            ? null
            : new CharMgrOwnerNote(noteId, "S:Folders", "1::F::0::Main|");
    }

    [Method("GetOwnerNoteLabels")]

    public async ValueTask<PyValue> GetOwnerNoteLabels(ServiceCall call)
    {
        var data = await ReadPersonalDataAsync(call);
        var notes = data.OwnerNotes
            .Where(note => note.NoteId > 0)
            .OrderBy(note => note.NoteId)
            .ToList();
        if (call.Session.CharacterId is > 0 && DefaultFoldersNote(data) is { } defaultFolders)
        {
            notes.Add(defaultFolders);
            notes.Sort((left, right) => left.NoteId.CompareTo(right.NoteId));
        }

        var rows = notes
            .Select(note => (PyValue)new PyList([new PyInt(note.NoteId), new PyStr(note.Label ?? "")]))
            .ToArray();
        return Py.Rowset(["noteID", "label"], rows);
    }

    /// <summary>An absent note is returned in the one-row KeyVal list expected by the client.</summary>
    [Method("GetOwnerNote")]

    public async ValueTask<PyValue> GetOwnerNote(ServiceCall call)
    {
        var noteId = IdOf(call.Arg(0)) ?? 0;
        var data = await ReadPersonalDataAsync(call);
        var note = data.OwnerNotes.FirstOrDefault(entry => entry.NoteId == noteId);
        if (note is null && call.Session.CharacterId is > 0 && DefaultFoldersNote(data) is { } defaultFolders &&
            defaultFolders.NoteId == noteId)
        {
            note = defaultFolders;
        }

        return new PyList([Py.KeyVal(
            ("noteID", new PyInt(noteId)),
            ("label", new PyStr(note?.Label ?? "")),
            ("note", new PyStr(note?.Note ?? "")))]);
    }

    [Method("GetNote")]

    public async ValueTask<PyValue> GetNote(ServiceCall call)
    {
        var itemId = IdOf(call.Arg(0)) ?? 0;
        var data = await ReadPersonalDataAsync(call);
        return new PyStr(data.ItemNotes.GetValueOrDefault(itemId, ""));
    }

    [Method("GetRecentShipKillsAndLosses")]
    public PyValue GetRecentShipKillsAndLosses(ServiceCall call) => new PyList([]);
}
