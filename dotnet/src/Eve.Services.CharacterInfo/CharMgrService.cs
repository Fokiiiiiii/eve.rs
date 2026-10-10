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
    public IReadOnlyDictionary<string, PyValue> CharacterSettings { get; init; } =
        new Dictionary<string, PyValue>(StringComparer.Ordinal);
}


public sealed record CharMgrOrganizationInfo(long CorporationId, long AllianceId, long WarFactionId);
[Service("charMgr")]
public sealed partial class CharMgrService(
    PgCharacterStore characters,
    StationCatalog stations,
    Func<long, int, int, CancellationToken, ValueTask<IReadOnlyList<PyValue>>> readRecentShipKillmailPage,
    Func<long, CancellationToken, ValueTask<string?>> readCharacterDescription,
    Func<long, string, CancellationToken, ValueTask> writeCharacterDescription,
    Func<long, CancellationToken, ValueTask<double>> readCharacterSecurityStatus,
    Func<long, CancellationToken, ValueTask<long?>> readMedal1GraphicId,
    Func<long, CancellationToken, ValueTask<CharMgrPersonalData?>>? readPersonalData = null,
    Func<long, Func<CharMgrPersonalData, CharMgrPersonalData>, CancellationToken, ValueTask<CharMgrPersonalData?>>? updatePersonalData = null,
    Func<long, long, string?, CancellationToken, ValueTask>? writeActivityStatus = null,
    Func<long, PyValue, CancellationToken, ValueTask>? writeLogSettings = null,
    Func<long, CancellationToken, ValueTask<long?>>? readCorporationMemberStartedMs = null,
    Func<IReadOnlyList<Character>, CancellationToken, ValueTask<IReadOnlyDictionary<long, CharMgrOrganizationInfo>>>? readOrganizationInfo = null)
{

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

    private static bool PageValue(PyValue value, out int pageValue)
    {
        pageValue = 0;
        var number = IdOf(value);
        if (number is null || number < 0 || number > int.MaxValue)
        {
            return false;
        }

        pageValue = (int)number.Value;
        return true;
    }

    private static PyValue FileTime(DateTimeOffset time) => new PyLong(Py.FileTimeFromUnixMs(time.ToUnixTimeMilliseconds()));

    private static PyValue Nullable(long? value) => value is { } id ? new PyInt(id) : None;

    /// <summary>The public record of a character: <c>util.KeyVal</c> of the fields the client's character windows read.</summary>
    internal static PyValue PublicInfo(Character c, string description, double securityStatus, long? medal1GraphicId)
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
            ("description", new PyStr(description)),
            ("securityRating", new PyInt(0)),
            ("securityStatus", Py.Number(securityStatus)),
            ("bounty", new PyInt(0)),
            ("title", new PyStr("")),
            ("shortName", new PyStr("none")),
            ("stationID", new PyInt(c.StationId)),
            ("solarSystemID", new PyInt(c.SolarSystemId)),
            ("militiaFactionID", None),
            ("medal1GraphicID", Nullable(medal1GraphicId)));
    }

    private async ValueTask<Character?> CharacterAsync(ServiceCall call)
    {
        var id = IdOf(call.Arg(0));
        return id is null ? null : await characters.GetAsync(id.Value, call.Cancellation);
    }

    private async ValueTask<PyValue> PublicInfoAsync(Character character, CancellationToken cancellationToken)
    {
        var description = await readCharacterDescription(character.Id, cancellationToken) ?? "";
        var securityStatus = await readCharacterSecurityStatus(character.Id, cancellationToken);
        var medal1GraphicId = await readMedal1GraphicId(character.Id, cancellationToken);
        return PublicInfo(character, description, securityStatus, medal1GraphicId);
    }

    [Method("GetPublicInfo")]
    public async ValueTask<PyValue> GetPublicInfo(ServiceCall call) =>
        await CharacterAsync(call) is { } character
            ? await PublicInfoAsync(character, call.Cancellation)
            : None;

    /// <summary>The same record in a list (the client asks for it with a second argument for the character window).</summary>
    [Method("GetPublicInfo3")]
    public async ValueTask<PyValue> GetPublicInfo3(ServiceCall call) =>
        await CharacterAsync(call) is { } character
            ? new PyList([await PublicInfoAsync(character, call.Cancellation)])
            : None;

    [Method("GetCharacterDescription")]
    public async ValueTask<PyValue> GetCharacterDescription(ServiceCall call)
    {
        if (await CharacterAsync(call) is not { } character)
        {
            return None;
        }

        return new PyStr(await readCharacterDescription(character.Id, call.Cancellation) ?? "");
    }

    [Method("SetCharacterDescription")]
    public async ValueTask<PyValue> SetCharacterDescription(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } characterId || characterId <= 0 ||
            call.Args.Count == 0 ||
            await characters.GetAsync(characterId, call.Cancellation) is null)
        {
            return None;
        }

        var description = Eve.Macho.JsSemantics.StringValue(call.Arg(0));
        if (description is null)
        {
            return None;
        }

        await writeCharacterDescription(characterId, description, call.Cancellation);
        return None;
    }

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


    [Method("MachoResolveObject")]
    public PyValue MachoResolveObject(ServiceCall call)
    {
        const long globalAssetsContainerId = 10002;
        IReadOnlyList<PyValue> parameters = call.Arg(0) switch
        {
            PyTuple tuple => tuple.Items,
            PyList list => list.Items,
            _ => [],
        };
        var sessionCharacterId = call.Session.CharacterId.GetValueOrDefault();
        var characterId = parameters.Count > 0
            ? IdOf(parameters[0]) ?? sessionCharacterId
            : sessionCharacterId;
        var containerId = parameters.Count > 1 ? IdOf(parameters[1]) : null;
        return characterId > 0 && containerId == globalAssetsContainerId
            ? new PyInt(Eve.Macho.MachoMessage.ProxyNodeId)
            : None;
    }

    [Method("GetPaperdollState")]
    public PyValue GetPaperdollState(ServiceCall call) => new PyInt(0);

    [Method("GetCohortsForCharacter")]
    public PyValue GetCohortsForCharacter(ServiceCall call) => new PyList([]);

    [Method("GetSettingsInfo")]
    public PyValue GetSettingsInfo(ServiceCall call) => Py.Tuple(new PyBuffer(SettingsCode), new PyInt(0));

    [Method("GetCharacterSettings")]
    public async ValueTask<PyValue> GetCharacterSettings(ServiceCall call)
    {
        var settings = await ReadPersonalDataAsync(call);
        return new PyDict([.. settings.CharacterSettings
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new KeyValuePair<PyValue, PyValue>(new PyStr(entry.Key), entry.Value))]);
    }

    [Method("SaveCharacterSetting")]
    public async ValueTask<PyValue> SaveCharacterSetting(ServiceCall call)
    {
        var key = OptionalText(call.Arg(0))?.Trim();
        if (string.IsNullOrEmpty(key))
            return None;

        var value = call.Arg(1);
        if (value is PyStr text && text.Value.Length > 102400 ||
            value is PyWString wideText && wideText.Value.Length > 102400)
            throw MachoErrors.ThrowUserError("CustomInfo", ("info", new PyStr("Character settings are limited to 100 KB.")));

        await UpdatePersonalDataAsync(call, data =>
        {
            var settings = new Dictionary<string, PyValue>(data.CharacterSettings, StringComparer.Ordinal)
            {
                [key] = value,
            };
            return data with { CharacterSettings = settings };
        });
        return None;
    }

    [Method("DeleteCharacterSetting")]
    public async ValueTask<PyValue> DeleteCharacterSetting(ServiceCall call)
    {
        var key = OptionalText(call.Arg(0))?.Trim();
        if (string.IsNullOrEmpty(key))
            return None;

        await UpdatePersonalDataAsync(call, data =>
        {
            var settings = new Dictionary<string, PyValue>(data.CharacterSettings, StringComparer.Ordinal);
            settings.Remove(key);
            return data with { CharacterSettings = settings };
        });
        return None;
    }

    private async ValueTask<CharMgrPersonalData> ReadPersonalDataAsync(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } characterId || characterId <= 0 || readPersonalData is null)
        {
            return CharMgrPersonalData.Empty;
        }

        return await readPersonalData(characterId, call.Cancellation) ?? CharMgrPersonalData.Empty;
    }

    private async ValueTask<CharMgrPersonalData?> UpdatePersonalDataAsync(
        ServiceCall call, Func<CharMgrPersonalData, CharMgrPersonalData> update)
    {
        if (call.Session.CharacterId is not { } characterId || characterId <= 0 ||
            updatePersonalData is null ||
            await characters.GetAsync(characterId, call.Cancellation) is null)
        {
            return null;
        }

        return await updatePersonalData(characterId, update, call.Cancellation);
    }

    private static long NormalizeInteger(PyValue value, long fallback = 0)
    {
        switch (value)
        {
            case PyBool boolean:
                return boolean.Value ? 1 : 0;
            case PyInt number:
                return number.Value;
            case PyLong number:
                return number.Value;
            case PyReal number when double.IsFinite(number.Value) &&
                number.Value >= long.MinValue && number.Value <= long.MaxValue:
                return (long)Math.Truncate(number.Value);
            case PyNone:
                return fallback;
        }

        var text = Eve.Macho.JsSemantics.StringValue(value);
        return double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) &&
            double.IsFinite(parsed) && parsed >= long.MinValue && parsed <= long.MaxValue
                ? (long)Math.Truncate(parsed)
                : fallback;
    }

    private static bool NormalizeBoolean(PyValue value, bool fallback = false)
    {
        switch (value)
        {
            case PyBool boolean:
                return boolean.Value;
            case PyInt number:
                return number.Value != 0;
            case PyLong number:
                return number.Value != 0;
            case PyReal number:
                return number.Value != 0 && !double.IsNaN(number.Value);
            case PyNone:
                return fallback;
        }

        var text = Eve.Macho.JsSemantics.StringValue(value);
        if (text is not null)
        {
            var normalized = text.Trim().ToLowerInvariant();
            if (normalized.Length == 0)
            {
                return fallback;
            }
            if (normalized is "false" or "0")
            {
                return false;
            }
            if (normalized is "true" or "1")
            {
                return true;
            }
            return true;
        }

        return value is PyDict or PyList or PyTuple or PyObjectEx or PyObject;
    }

    private static IReadOnlyList<PyValue> ListItems(PyValue value) => value switch
    {
        PyList list => list.Items,
        PyTuple tuple => tuple.Items,
        _ => [],
    };

    private static string? OptionalText(PyValue? value)
    {
        if (value is null or PyNone)
        {
            return null;
        }

        return value switch
        {
            PyBool boolean => boolean.Value ? "true" : "false",
            PyInt number => number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            PyLong number => number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            PyReal number => number.Value.ToString("G", System.Globalization.CultureInfo.InvariantCulture),
            _ => Eve.Macho.JsSemantics.StringValue(value) ?? value.ToString(),
        };
    }

    private static long NextOwnerNoteId(CharMgrPersonalData data, IEnumerable<CharMgrOwnerNote> notes)
    {
        var used = notes.Select(note => note.NoteId).ToHashSet();
        var next = data.NextOwnerNoteId is > 0 ? data.NextOwnerNoteId.Value : 1;
        while (used.Contains(next) && next < long.MaxValue)
        {
            next++;
        }
        return next > 0 && next < long.MaxValue ? next : 0;
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


    [Method("GetPrivateInfoOnCorpChange")]
    public async ValueTask<PyValue> GetPrivateInfoOnCorpChange(ServiceCall call)
    {
        var characterId = call.Args.Count == 0
            ? call.Session.CharacterId ?? 0
            : NormalizeInteger(call.Arg(0));
        var character = characterId > 0
            ? await characters.GetAsync(characterId, call.Cancellation)
            : null;
        var corporationId = character?.CorporationId ??
            call.Session.GetInt("corporationID") ??
            call.Session.GetInt("corpid") ??
            0;
        var startedMs = characterId > 0 && readCorporationMemberStartedMs is not null
            ? await readCorporationMemberStartedMs(characterId, call.Cancellation)
            : null;
        var corporationDateTime = startedMs is > 0
            ? new PyLong(Py.FileTimeFromUnixMs(startedMs.Value))
            : character is not null ? FileTime(character.CreatedAt) : None;

        return Py.KeyVal(
            ("corporationID", corporationId > 0 ? new PyInt(corporationId) : None),
            ("corporationDateTime", corporationDateTime));
    }


    [Method("GetOrganizationInfoForCharacters")]
    public async ValueTask<PyValue> GetOrganizationInfoForCharacters(ServiceCall call)
    {
        var characterIds = ListItems(call.Arg(0))
            .Select(value => NormalizeInteger(value))
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        if (characterIds.Length == 0)
        {
            return new PyDict([]);
        }

        var characterRows = await characters.GetManyAsync(characterIds, call.Cancellation);
        var charactersById = characterRows.ToDictionary(character => character.Id);
        var organizationInfo = readOrganizationInfo is null
            ? new Dictionary<long, CharMgrOrganizationInfo>()
            : await readOrganizationInfo(characterRows, call.Cancellation);

        var entries = new List<KeyValuePair<PyValue, PyValue>>(characterIds.Length);
        foreach (var characterId in characterIds)
        {
            var corporationId = 0L;
            var allianceId = 0L;
            var warFactionId = 0L;
            if (charactersById.TryGetValue(characterId, out var character))
            {
                corporationId = character.CorporationId;
                allianceId = character.AllianceId ?? 0;
                if (organizationInfo.TryGetValue(characterId, out var organization))
                {
                    corporationId = organization.CorporationId;
                    allianceId = organization.AllianceId;
                    warFactionId = organization.WarFactionId;
                }
            }

            entries.Add(new KeyValuePair<PyValue, PyValue>(
                new PyInt(characterId),
                Py.KeyVal(
                    ("corporationID", new PyInt(corporationId)),
                    ("allianceID", new PyInt(allianceId)),
                    ("warFactionID", new PyInt(warFactionId)))));
        }
        return new PyDict(entries);
    }

    [Method("AddContact")]
    public async ValueTask<PyValue> AddContact(ServiceCall call)
    {
        var contactId = NormalizeInteger(call.Arg(0));
        var relationshipId = call.Args.Count > 1 ? NormalizeInteger(call.Arg(1)) : 0;
        var inWatchlist = call.Args.Count > 2 && NormalizeBoolean(call.Arg(2));
        await UpdatePersonalDataAsync(call, data =>
        {
            if (contactId <= 0)
            {
                return data;
            }

            var existing = data.Contacts.FirstOrDefault(contact => contact.ContactId == contactId);
            var contact = new CharMgrContact(contactId, inWatchlist, relationshipId, existing?.LabelMask ?? 0);
            return data with
            {
                Contacts = data.Contacts.Where(item => item.ContactId != contactId)
                    .Append(contact).OrderBy(item => item.ContactId).ToArray(),
            };
        });
        return None;
    }

    [Method("AddOwnerNote")]
    public async ValueTask<PyValue> AddOwnerNote(ServiceCall call)
    {
        var label = OptionalText(call.Args.Count > 0 ? call.Arg(0) : new PyStr("")) ?? "";
        var noteText = OptionalText(call.Args.Count > 1 ? call.Arg(1) : new PyStr("")) ?? "";
        var noteId = 0L;
        await UpdatePersonalDataAsync(call, data =>
        {
            var id = NextOwnerNoteId(data, data.OwnerNotes);
            if (id <= 0)
            {
                return data;
            }

            noteId = id;
            return data with
            {
                OwnerNotes = [.. data.OwnerNotes, new CharMgrOwnerNote(id, label, noteText)],
                NextOwnerNoteId = id + 1,
            };
        });
        return new PyInt(noteId);
    }

    [Method("BlockOwners")]
    public async ValueTask<PyValue> BlockOwners(ServiceCall call)
    {
        var ownerIds = ListItems(call.Arg(0)).Select(item => NormalizeInteger(item)).Where(id => id > 0).ToHashSet();
        await UpdatePersonalDataAsync(call, data => data with
        {
            BlockedSenders = data.BlockedSenders.Concat(ownerIds).Distinct().Order().ToArray(),
        });
        return None;
    }

    [Method("DeleteContacts")]
    public async ValueTask<PyValue> DeleteContacts(ServiceCall call)
    {
        var contactIds = ListItems(call.Arg(0)).Select(item => NormalizeInteger(item)).ToHashSet();
        await UpdatePersonalDataAsync(call, data => data with
        {
            Contacts = data.Contacts.Where(contact => !contactIds.Contains(contact.ContactId)).ToArray(),
        });
        return None;
    }

    [Method("EditContactsRelationshipID")]
    public async ValueTask<PyValue> EditContactsRelationshipID(ServiceCall call)
    {
        var contactIds = ListItems(call.Arg(0)).Select(item => NormalizeInteger(item)).Where(id => id > 0).ToHashSet();
        var relationshipId = NormalizeInteger(call.Arg(1));
        await UpdatePersonalDataAsync(call, data =>
        {
            var contacts = data.Contacts.ToDictionary(contact => contact.ContactId);
            foreach (var contactId in contactIds)
            {
                var existing = contacts.GetValueOrDefault(contactId);
                contacts[contactId] = new CharMgrContact(
                    contactId,
                    existing?.InWatchlist ?? false,
                    relationshipId,
                    existing?.LabelMask ?? 0);
            }
            return data with { Contacts = contacts.Values.OrderBy(contact => contact.ContactId).ToArray() };
        });
        return None;
    }

    [Method("EditOwnerNote")]
    public async ValueTask<PyValue> EditOwnerNote(ServiceCall call)
    {
        var noteId = call.Args.Count > 0 ? NormalizeInteger(call.Arg(0)) : 0;
        var label = call.Args.Count > 1 ? OptionalText(call.Arg(1)) : null;
        var noteText = call.Args.Count > 2 ? OptionalText(call.Arg(2)) : null;
        await UpdatePersonalDataAsync(call, data =>
        {
            var notes = data.OwnerNotes.ToList();
            var index = notes.FindIndex(note => note.NoteId == noteId);
            if (noteId <= 0 || index < 0)
            {
                return data;
            }

            var previous = notes[index];
            var updated = previous with
            {
                Label = label ?? previous.Label,
                Note = noteText ?? previous.Note,
            };
            notes[index] = updated;
            var nextNoteId = data.NextOwnerNoteId;
            if (previous.Label == "S:Folders" && updated.Label != "S:Folders" &&
                !notes.Any(note => note.Label == "S:Folders"))
            {
                var foldersId = NextOwnerNoteId(data, notes);
                if (foldersId > 0)
                {
                    notes.Add(new CharMgrOwnerNote(foldersId, "S:Folders", "1::F::0::Main|"));
                    nextNoteId = foldersId + 1;
                }
            }

            return data with
            {
                OwnerNotes = notes.OrderBy(note => note.NoteId).ToArray(),
                NextOwnerNoteId = nextNoteId,
            };
        });
        return None;
    }



    [Method("LogSettings")]
    public async ValueTask<PyValue> LogSettings(ServiceCall call)
    {
        if (call.Session.CharacterId is { } characterId && characterId > 0 &&
            writeLogSettings is not null &&
            await characters.GetAsync(characterId, call.Cancellation) is not null)
        {
            var settings = call.Arg(0);
            if (settings is not (PyDict or PyList or PyTuple or PyObjectEx or PyObject))
            {
                settings = new PyDict([]);
            }
            await writeLogSettings(characterId, settings, call.Cancellation);
        }
        return None;
    }

    [Method("RemoveOwnerNote")]
    public async ValueTask<PyValue> RemoveOwnerNote(ServiceCall call)
    {
        var noteId = call.Args.Count > 0 ? NormalizeInteger(call.Arg(0)) : 0;
        await UpdatePersonalDataAsync(call, data =>
        {
            var notes = data.OwnerNotes.ToList();
            var index = notes.FindIndex(note => note.NoteId == noteId);
            if (noteId <= 0 || index < 0)
            {
                return data;
            }

            var existing = notes[index];
            if (existing.Label == "S:Folders" &&
                notes.Count(note => note.Label == "S:Folders") <= 1)
            {
                notes[index] = existing with { Note = "1::F::0::Main|" };
                return data with { OwnerNotes = notes.ToArray() };
            }

            notes.RemoveAt(index);
            var nextNoteId = data.NextOwnerNoteId;
            if (existing.Label == "S:Folders" && !notes.Any(note => note.Label == "S:Folders"))
            {
                var foldersId = NextOwnerNoteId(data, notes);
                if (foldersId > 0)
                {
                    notes.Add(new CharMgrOwnerNote(foldersId, "S:Folders", "1::F::0::Main|"));
                    nextNoteId = foldersId + 1;
                }
            }
            return data with
            {
                OwnerNotes = notes.OrderBy(note => note.NoteId).ToArray(),
                NextOwnerNoteId = nextNoteId,
            };
        });
        return None;
    }

    [Method("SetActivityStatus")]
    public async ValueTask<PyValue> SetActivityStatus(ServiceCall call)
    {
        if (call.Session.CharacterId is { } characterId && characterId > 0 &&
            writeActivityStatus is not null &&
            await characters.GetAsync(characterId, call.Cancellation) is not null)
        {
            var status = NormalizeInteger(call.Kwarg("status") ?? call.Arg(0));
            var extraInfo = call.Kwarg("extraInfo") ?? call.Arg(1);
            await writeActivityStatus(characterId, status, OptionalText(extraInfo), call.Cancellation);
        }
        return None;
    }

    [Method("SetNote")]
    public async ValueTask<PyValue> SetNote(ServiceCall call)
    {
        var itemId = call.Args.Count > 0 ? NormalizeInteger(call.Arg(0)) : 0;
        var noteText = OptionalText(call.Args.Count > 1 ? call.Arg(1) : new PyStr("")) ?? "";
        await UpdatePersonalDataAsync(call, data =>
        {
            if (itemId <= 0)
            {
                return data;
            }

            var notes = new Dictionary<long, string>(data.ItemNotes);
            if (noteText.Length == 0)
            {
                notes.Remove(itemId);
            }
            else
            {
                notes[itemId] = noteText;
            }
            return data with { ItemNotes = notes };
        });
        return None;
    }

    [Method("UnblockOwners")]
    public async ValueTask<PyValue> UnblockOwners(ServiceCall call)
    {
        var ownerIds = ListItems(call.Arg(0)).Select(item => NormalizeInteger(item)).ToHashSet();
        await UpdatePersonalDataAsync(call, data => data with
        {
            BlockedSenders = data.BlockedSenders.Where(id => !ownerIds.Contains(id)).ToArray(),
        });
        return None;
    }

    [Method("GetRecentShipKillsAndLosses")]
    public async ValueTask<PyValue> GetRecentShipKillsAndLosses(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } characterId || characterId <= 0 ||
            await characters.GetAsync(characterId, call.Cancellation) is null)
        {
            return None;
        }

        if (call.Args.Count < 2 ||
            !PageValue(call.Arg(0), out var num) ||
            !PageValue(call.Arg(1), out var startIndex) ||
            num == 0)
        {
            return new PyList([]);
        }

        return new PyList(await readRecentShipKillmailPage(characterId, num, startIndex, call.Cancellation));
    }
}


[Service("home_station")]
public sealed partial class HomeStationService(PgCharacterStore characters, StationCatalog stations)
{
    [Method("get_home_station")]
    public async ValueTask<PyValue> GetHomeStation(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } characterId ||
            await characters.GetAsync(characterId, call.Cancellation) is not { } character)
            throw new InvalidOperationException("The current character has no home-station record.");

        var station = stations.Get(character.HomeStationId);
        var isFallback = false;
        if (station is null)
        {
            station = stations.Get(character.StationId);
            isFallback = station is not null;
        }
        if (station is null)
            throw new InvalidOperationException($"Home station {character.HomeStationId} is unavailable.");

        var header = Py.Tuple(
            Py.Tuple(new PyToken("homestation.types.StationData")),
            Py.Dict(
                ("is_fallback", isFallback ? PyBool.True : PyBool.False),
                ("solar_system_id", new PyInt(station.SolarSystemId)),
                ("id", new PyInt(station.Id)),
                ("type_id", new PyInt(station.TypeId))));
        return new PyObjectEx(ObjectExKind.Ex2, header, [], []);
    }
}
