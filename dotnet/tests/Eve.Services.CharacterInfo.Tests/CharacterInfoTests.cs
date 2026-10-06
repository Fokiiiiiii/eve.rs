using Eve.Characters;
using Eve.Data;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Wallet;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;

namespace Eve.Services.CharacterInfo.Tests;

/// <summary>Skips a test, loudly, when there is no PostgreSQL to test against (see tools/dev/pg18.sh).</summary>
public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute()
    {
        if (Rig.Url is null)
        {
            Skip = "EVE_PG_URL is not set (see tools/dev/pg18.sh)";
        }
    }
}

internal sealed class Rig : IAsyncDisposable
{
    public static readonly string? Url = Environment.GetEnvironmentVariable("EVE_PG_URL");

    public static readonly StationCatalog Stations = new([
        new Station(60015249, 92885, "Manifest V - AIR Laboratories Trade Center", 1000413, "AIR Laboratories", 500028, "Association for Interdisciplinary Research", 123, 40491241,
            30100032, "Manifest", 20010001, "Manifest District", 10001004, "Exordium", 1.0, 0.05, 4),
        new Station(60000610, 4023, "Paara I - Deep Core Mining Inc. Mining Outpost", 1000006, "Deep Core Mining Inc.", 500001, "Caldari State", 7, 40003891,
            30002775, "Paara", 20000407, "Peyiri", 10000033, "The Citadel", 0.7785, 0.05, 4),
    ]);

    private readonly string _characters = $"ci_{Guid.NewGuid():N}"[..20];
    private readonly string _wallet = $"cw_{Guid.NewGuid():N}"[..20];
    private readonly Dictionary<long, string> _descriptions = [];
    private readonly Dictionary<long, double> _securityStatuses = [];
    private readonly Dictionary<long, long?> _medalGraphicIds = [];

    private Rig(NpgsqlDataSource source)
    {
        Source = source;
        Characters = new PgCharacterStore(source, _characters);
        Wallets = new PgWalletStore(source, _wallet);
        CharMgr = CreateCharMgr();
        Account = new AccountService(Wallets);
    }

    public List<PyValue> KillmailRecords { get; } = [];

    public (long CharacterId, int Num, int StartIndex)? LastKillmailPage { get; private set; }

    public Dictionary<long, string> Descriptions => _descriptions;

    public Dictionary<long, double> SecurityStatuses => _securityStatuses;

    public Dictionary<long, long?> MedalGraphicIds => _medalGraphicIds;

    public CharMgrService CreateCharMgr(Func<long, CancellationToken, ValueTask<CharMgrPersonalData?>>? readPersonalData = null) =>
        new(Characters, Stations, ReadRecentShipKillmailPageAsync, ReadCharacterDescriptionAsync,
            WriteCharacterDescriptionAsync, ReadSecurityStatusAsync, ReadMedal1GraphicIdAsync, readPersonalData);

    private ValueTask<IReadOnlyList<PyValue>> ReadRecentShipKillmailPageAsync(long characterId, int num, int startIndex, CancellationToken _)
    {
        LastKillmailPage = (characterId, num, startIndex);
        IReadOnlyList<PyValue> page = startIndex >= KillmailRecords.Count
            ? Array.Empty<PyValue>()
            : KillmailRecords.Skip(startIndex).Take(num).ToArray();
        return ValueTask.FromResult(page);
    }

    private ValueTask<string?> ReadCharacterDescriptionAsync(long characterId, CancellationToken _) =>
        ValueTask.FromResult(_descriptions.TryGetValue(characterId, out var description) ? description : null);

    private ValueTask WriteCharacterDescriptionAsync(long characterId, string description, CancellationToken _)
    {
        _descriptions[characterId] = description;
        return ValueTask.CompletedTask;
    }

    private ValueTask<double> ReadSecurityStatusAsync(long characterId, CancellationToken _) =>
        ValueTask.FromResult(_securityStatuses.GetValueOrDefault(characterId));

    private ValueTask<long?> ReadMedal1GraphicIdAsync(long characterId, CancellationToken _) =>
        ValueTask.FromResult(_medalGraphicIds.GetValueOrDefault(characterId));

    public NpgsqlDataSource Source { get; }

    public PgCharacterStore Characters { get; }

    public PgWalletStore Wallets { get; }

    public CharMgrService CharMgr { get; }

    public AccountService Account { get; }

    public static async Task<Rig> CreateAsync()
    {
        CharacterNames.EnsureUnicodeSupport();
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Url!));
        var rig = new Rig(source);
        await PgCharacterStore.MigrateAsync(source, rig._characters);
        await PgWalletStore.MigrateAsync(source, rig._wallet);
        return rig;
    }

    /// <summary>A character of a school, with a wallet, docked at a station.</summary>
    public async Task<long> MakeAsync(string name, int account = 1, long corporation = 1000006, long? faction = 500001, long station = 60015249, decimal isk = 100_000m)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await Characters.InsertAsync(connection, transaction,
            new NewCharacter(account, name, 1, 1, 1, 10, 35, 1373, corporation, faction, station, 30100032, 20010001, 10001004, null, null));
        await Wallets.OpenAsync(connection, transaction, id, isk, 2_222);
        await transaction.CommitAsync();
        return id;
    }

    public static (ClientSession Session, IPacketSink Sink) Session(long? characterId)
    {
        var session = new ClientSession { SessionId = 1, ClientId = 2, UserId = 34 };
        if (characterId is { } id)
        {
            session.CharacterId = id;
        }
        return (session, session.Sink!);
    }

    public static async Task<PyValue> CallAsync(IService service, ClientSession session, string method, params PyValue[] args) =>
        await service.CallAsync(new ServiceCall { Method = method, Args = args, Session = session });

    public async ValueTask DisposeAsync()
    {
        foreach (var schema in new[] { _characters, _wallet })
        {
            await using var command = Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await command.ExecuteNonQueryAsync();
        }
        await Source.DisposeAsync();
    }
}

public class EmpireTests
{
    [Theory]
    [InlineData(1000044L, 500001L, 500001L)]
    [InlineData(1000115L, null, 500002L)]
    [InlineData(1000009L, 500003L, 500003L)]
    [InlineData(1000006L, 500001L, 500004L)] // the reference shows a Caldari school's pupils under the Gallente empire
    [InlineData(1000094L, 500004L, 500004L)] // another corporation: its faction
    [InlineData(1000166L, 500003L, 500003L)]
    public void The_empire_is_the_references_table_else_the_faction(long corporation, long? faction, long expected) =>
        Assert.Equal(expected, CharMgrService.EmpireOf(corporation, faction));

    [Fact]
    public void A_corporation_without_a_faction_or_an_entry_has_no_empire() => Assert.Equal(0, CharMgrService.EmpireOf(1000999, null));
}

public class CharMgrTests
{
    private static PyValue Id(long value) => new PyInt(value);

    private static Dictionary<string, PyValue> Fields(PyValue keyVal) =>
        ((PyDict)((PyObject)keyVal).Args).Entries.ToDictionary(e => JsSemantics.StringValue(e.Key)!, e => e.Value);

    private static string[] Order(PyValue keyVal) => [.. ((PyDict)((PyObject)keyVal).Args).Entries.Select(e => JsSemantics.StringValue(e.Key)!)];

    [PgFact]
    public async Task The_public_record_has_the_recorded_fields_in_the_recorded_order()
    {
        await using var rig = await Rig.CreateAsync();
        var id = await rig.MakeAsync("Ann Lee");
        var (session, _) = Rig.Session(id);
        var record = await Rig.CallAsync(rig.CharMgr, session, "GetPublicInfo", Id(id));

        Assert.Equal(
            ["characterID", "characterName", "typeID", "raceID", "bloodlineID", "ancestryID", "corporationID", "allianceID", "factionID", "empireID", "schoolID",
             "gender", "createDateTime", "startDateTime", "description", "securityRating", "securityStatus", "bounty", "title", "shortName", "stationID",
             "solarSystemID", "militiaFactionID", "medal1GraphicID"],
            Order(record));
        var f = Fields(record);
        Assert.Equal((Id(id), new PyStr("Ann Lee"), Id(1373), Id(1), Id(1), Id(10)), (f["characterID"], f["characterName"], f["typeID"], f["raceID"], f["bloodlineID"], f["ancestryID"]));
        Assert.Equal((Id(1000006), PyNone.Instance, Id(500001), Id(500004), Id(35), Id(1)), (f["corporationID"], f["allianceID"], f["factionID"], f["empireID"], f["schoolID"], f["gender"]));
        Assert.Equal((Id(60015249), Id(30100032)), (f["stationID"], f["solarSystemID"]));
        Assert.Equal(f["createDateTime"], f["startDateTime"]);
        Assert.IsType<PyLong>(f["createDateTime"]);
        Assert.Equal((new PyStr(""), Id(0), Id(0), Id(0), new PyStr(""), new PyStr("none")), (f["description"], f["securityRating"], f["securityStatus"], f["bounty"], f["title"], f["shortName"]));
        Assert.Equal((PyNone.Instance, PyNone.Instance), (f["militiaFactionID"], f["medal1GraphicID"]));
    }

    [PgFact]
    public async Task Another_players_record_is_readable_and_an_unknown_id_is_not_made_up()
    {
        await using var rig = await Rig.CreateAsync();
        var mine = await rig.MakeAsync("Ann Lee");
        var theirs = await rig.MakeAsync("Bob Roe", account: 2, corporation: 1000094, faction: 500004);
        var (session, _) = Rig.Session(mine);

        Assert.Equal(new PyStr("Bob Roe"), Fields(await Rig.CallAsync(rig.CharMgr, session, "GetPublicInfo", Id(theirs)))["characterName"]);
        Assert.Equal(Id(500004), Fields(await Rig.CallAsync(rig.CharMgr, session, "GetPublicInfo", Id(theirs)))["empireID"]); // its faction
        foreach (var unknown in new PyValue[] { Id(1), Id(999_999_999), new PyStr("x"), PyNone.Instance })
        {
            Assert.Equal(PyNone.Instance, await Rig.CallAsync(rig.CharMgr, session, "GetPublicInfo", unknown));
        }
        Assert.Equal(PyNone.Instance, await Rig.CallAsync(rig.CharMgr, session, "GetPublicInfo"));
    }

    [PgFact]
    public async Task The_record_can_be_asked_for_in_a_list_and_by_a_long_id()
    {
        await using var rig = await Rig.CreateAsync();
        var id = await rig.MakeAsync("Ann Lee");
        var (session, _) = Rig.Session(id);
        var single = await Rig.CallAsync(rig.CharMgr, session, "GetPublicInfo", Id(id));
        Assert.Equal(new PyList([single]), await Rig.CallAsync(rig.CharMgr, session, "GetPublicInfo3", Id(id)));
        Assert.Equal(new PyList([single]), await Rig.CallAsync(rig.CharMgr, session, "GetPublicInfo3", Id(id), Id(0)));
        Assert.Equal(single, await Rig.CallAsync(rig.CharMgr, session, "GetPublicInfo", new PyLong(id)));
        Assert.Equal(PyNone.Instance, await Rig.CallAsync(rig.CharMgr, session, "GetPublicInfo3", Id(1)));
    }

    [PgFact]
    public async Task Character_description_is_read_and_written_through_the_persistence_provider()
    {
        await using var rig = await Rig.CreateAsync();
        var id = await rig.MakeAsync("Ann Lee");
        rig.Descriptions[id] = "Original biography";
        var (session, _) = Rig.Session(id);

        Assert.Equal(new PyStr("Original biography"),
            await Rig.CallAsync(rig.CharMgr, session, "GetCharacterDescription", Id(id)));
        Assert.Equal(PyNone.Instance,
            await Rig.CallAsync(rig.CharMgr, session, "SetCharacterDescription", new PyWString("Updated biography ✓")));
        Assert.Equal(new PyStr("Updated biography ✓"),
            await Rig.CallAsync(rig.CharMgr, session, "GetCharacterDescription", Id(id)));
        Assert.Equal(PyNone.Instance,
            await Rig.CallAsync(rig.CharMgr, session, "SetCharacterDescription", new PyStr("ASCII bio")));
        Assert.Equal(new PyStr("ASCII bio"),
            await Rig.CallAsync(rig.CharMgr, session, "GetCharacterDescription", Id(id)));
        Assert.Equal(PyNone.Instance,
            await Rig.CallAsync(rig.CharMgr, session, "GetCharacterDescription", Id(1)));

        var (unknownSession, _) = Rig.Session(1);
        Assert.Equal(PyNone.Instance,
            await Rig.CallAsync(rig.CharMgr, unknownSession, "SetCharacterDescription", new PyWString("ignored")));
        Assert.False(rig.Descriptions.ContainsKey(1));

        var created = ((PyLong)await Rig.CallAsync(rig.CharMgr, session, "GetCharacterCreationDate", Id(id))).Value;
        var now = Py.FileTimeFromUnixMs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.InRange(created, now - (5 * 60 * 10_000_000L), now + (60 * 10_000_000L));
        Assert.Equal(PyNone.Instance, await Rig.CallAsync(rig.CharMgr, session, "GetCharacterCreationDate", Id(1)));
    }

    [PgFact]
    public async Task The_home_station_row_names_the_station_in_every_spelling()
    {
        await using var rig = await Rig.CreateAsync();
        var id = await rig.MakeAsync("Ann Lee");
        var (session, _) = Rig.Session(id);
        var row = await Rig.CallAsync(rig.CharMgr, session, "GetHomeStationRow");

        Assert.Equal(
            ["id", "station_id", "stationID", "home_station_id", "type_id", "typeID", "station_type_id", "name", "station_name", "stationName", "solar_system_id",
             "solarSystemID", "constellation_id", "constellationID", "region_id", "regionID", "owner_id", "ownerID", "clone_station_id", "cloneStationID",
             "is_fallback", "isFallback", "stationTypeID"],
            Order(row));
        var f = Fields(row);
        Assert.Equal(new PyStr("Manifest V - AIR Laboratories Trade Center"), f["name"]);
        Assert.All(new[] { "id", "station_id", "stationID", "home_station_id", "clone_station_id", "cloneStationID" }, k => Assert.Equal(Id(60015249), f[k]));
        Assert.All(new[] { "type_id", "typeID", "station_type_id", "stationTypeID" }, k => Assert.Equal(Id(92885), f[k]));
        Assert.Equal((Id(30100032), Id(20010001), Id(10001004), Id(1000413)), (f["solarSystemID"], f["constellationID"], f["regionID"], f["ownerID"]));
        Assert.Equal((PyBool.False, PyBool.False), (f["is_fallback"], f["isFallback"]));
    }

    [PgFact]
    public async Task Without_a_character_or_a_known_station_there_is_no_home_station_row()
    {
        await using var rig = await Rig.CreateAsync();
        var (nobody, _) = Rig.Session(null);
        Assert.Equal(PyNone.Instance, await Rig.CallAsync(rig.CharMgr, nobody, "GetHomeStationRow"));

        var lost = await rig.MakeAsync("Lost Pilot", station: 60099999);
        var (session, _) = Rig.Session(lost);
        Assert.Equal(PyNone.Instance, await Rig.CallAsync(rig.CharMgr, session, "GetHomeStationRow"));
    }

    [PgFact]
    public async Task The_fixed_answers_are_the_recorded_ones()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _) = Rig.Session(await rig.MakeAsync("Ann Lee"));
        Task<PyValue> Ask(string method, params PyValue[] args) => Rig.CallAsync(rig.CharMgr, session, method, args);

        Assert.Equal(Id(0), await Ask("GetPaperdollState"));
        Assert.Equal(new PyList([]), await Ask("GetCohortsForCharacter"));

        var settings = (PyTuple)await Ask("GetSettingsInfo");
        Assert.Equal(Id(0), settings.Items[1]);
        Assert.Equal(
            "630000000000000000010000004300000073040000006900005328010000004e280000000028000000002800000000280000000073080000003c737472696e673e740100000066010000007300000000",
            Convert.ToHexStringLower(((PyBuffer)settings.Items[0]).Value.Span));

        var contacts = Fields(await Ask("GetContactList"));
        Assert.Equal(new[] { "addresses", "blocked" }, contacts.Keys);
        var addresses = (PyDict)((PyObject)contacts["addresses"]).Args;
        Assert.Equal(new PyList(new[] { "contactID", "inWatchlist", "relationshipID", "labelMask" }.Select(c => (PyValue)new PyStr(c)).ToList()), addresses.Entries[0].Value);
        Assert.Equal(new PyToken("util.Row"), addresses.Entries[2].Value);
        Assert.Equal(new PyList([]), addresses.Entries[3].Value);
        var blocked = (PyObject)contacts["blocked"];
        Assert.Equal("eve.common.script.sys.rowset.Rowset", JsSemantics.StringValue(blocked.Name));
        var blockedRows = (PyDict)blocked.Args;
        Assert.Equal(new PyList([new PyStr("senderID")]), blockedRows.Entries[0].Value);
        Assert.Equal(new PyList([]), blockedRows.Entries[3].Value);
    }

    [PgFact]
    public async Task Recent_killmail_pages_are_character_scoped_and_preserve_client_rows_and_page_boundaries()
    {
        await using var rig = await Rig.CreateAsync();
        var characterId = await rig.MakeAsync("Ann Lee");
        var (session, _) = Rig.Session(characterId);
        var rows = new[]
        {
            Py.KeyVal(("killID", Id(101)), ("finalCharacterID", Id(characterId)), ("victimCharacterID", Id(2001)), ("clientField", new PyStr("first"))),
            Py.KeyVal(("killID", Id(102)), ("finalCharacterID", Id(2002)), ("victimCharacterID", Id(characterId)), ("clientField", new PyStr("second"))),
            Py.KeyVal(("killID", Id(103)), ("finalCharacterID", Id(characterId)), ("victimCharacterID", Id(2003)), ("clientField", new PyStr("third"))),
        };
        rig.KillmailRecords.AddRange(rows);

        Assert.Equal(new PyList([rows[1], rows[2]]),
            await Rig.CallAsync(rig.CharMgr, session, "GetRecentShipKillsAndLosses", Id(2), Id(1)));
        Assert.Equal((characterId, 2, 1), rig.LastKillmailPage!.Value);

        Assert.Equal(new PyList([]),
            await Rig.CallAsync(rig.CharMgr, session, "GetRecentShipKillsAndLosses", Id(2), Id(3)));
        Assert.Equal((characterId, 2, 3), rig.LastKillmailPage!.Value);

        var lastRead = rig.LastKillmailPage;
        var (unbound, _) = Rig.Session(null);
        Assert.Equal(PyNone.Instance,
            await Rig.CallAsync(rig.CharMgr, unbound, "GetRecentShipKillsAndLosses", Id(1), Id(0)));
        Assert.Equal(lastRead, rig.LastKillmailPage);

        var (invalidCharacter, _) = Rig.Session(0);
        Assert.Equal(PyNone.Instance,
            await Rig.CallAsync(rig.CharMgr, invalidCharacter, "GetRecentShipKillsAndLosses", Id(1), Id(0)));
        Assert.Equal(lastRead, rig.LastKillmailPage);

        var (missingCharacter, _) = Rig.Session(long.MaxValue);
        Assert.Equal(PyNone.Instance,
            await Rig.CallAsync(rig.CharMgr, missingCharacter, "GetRecentShipKillsAndLosses", Id(1), Id(0)));
        Assert.Equal(lastRead, rig.LastKillmailPage);

        Assert.Equal(new PyList([]),
            await Rig.CallAsync(rig.CharMgr, session, "GetRecentShipKillsAndLosses", Id(0), Id(0)));
        Assert.Equal(lastRead, rig.LastKillmailPage);
    }

    [PgFact]
    public async Task Public_info_projects_character_description_security_and_medal_sources()
    {
        await using var rig = await Rig.CreateAsync();
        var characterId = await rig.MakeAsync("Ann Lee");
        rig.Descriptions[characterId] = "A saved bio";
        rig.SecurityStatuses[characterId] = -2.5;
        rig.MedalGraphicIds[characterId] = 9876;
        var (session, _) = Rig.Session(characterId);

        var records = (PyList)await Rig.CallAsync(rig.CharMgr, session, "GetPublicInfo3", Id(characterId));
        Assert.Single(records.Items);
        var fields = Fields(records.Items[0]);
        Assert.Equal(new PyStr("A saved bio"), fields["description"]);
        Assert.Equal(await Rig.CallAsync(rig.CharMgr, session, "GetCharacterCreationDate", Id(characterId)), fields["startDateTime"]);
        Assert.Equal(Py.Number(-2.5), fields["securityStatus"]);
        Assert.Equal(Id(9876), fields["medal1GraphicID"]);
    }

    [PgFact]
    public async Task Contacts_labels_and_notes_are_read_for_the_bound_character()
    {
        await using var rig = await Rig.CreateAsync();
        var first = await rig.MakeAsync("Ann Lee");
        var second = await rig.MakeAsync("Bob Lee");
        var snapshots = new Dictionary<long, CharMgrPersonalData>
        {
            [first] = new(
                [new CharMgrContact(2001, true, -10, 5)],
                [3001],
                new Dictionary<long, CharMgrContactLabel> { [4] = new("Fleet", 7) },
                [new CharMgrOwnerNote(1, "S:Folders", "1::F::0::Main|"), new CharMgrOwnerNote(2, "Briefing", "Dock at Perimeter")],
                new Dictionary<long, string> { [4001] = "Saved ship note" }),
            [second] = new(
                [new CharMgrContact(2002, false, -5, 0)],
                [],
                new Dictionary<long, CharMgrContactLabel> { [8] = new("Friends", 3) },
                [new CharMgrOwnerNote(4, "Personal", "Private note")],
                new Dictionary<long, string>())
            {
                NextOwnerNoteId = 5,
            }
        };
        var service = rig.CreateCharMgr(
            (ownerId, _) => ValueTask.FromResult(snapshots.GetValueOrDefault(ownerId)));

        async Task<PyValue> Ask(long ownerId, string method, params PyValue[] args)
        {
            var (session, _) = Rig.Session(ownerId);
            return await Rig.CallAsync(service, session, method, args);
        }

        Assert.Equal(
            Py.KeyVal(
                ("addresses", Py.Rowset(
                    ["contactID", "inWatchlist", "relationshipID", "labelMask"],
                    [new PyList([Id(2001), Id(1), Id(-10), Id(5)])])),
                ("blocked", Py.Rowset(["senderID"], [new PyList([Id(3001)])]))),
            await Ask(first, "GetContactList"));
        Assert.Equal(
            new PyDict([new KeyValuePair<PyValue, PyValue>(
                Id(4), Py.KeyVal(("labelID", Id(4)), ("name", new PyStr("Fleet")), ("color", Id(7))))]),
            await Ask(first, "GetLabels"));
        Assert.Equal(
            Py.Rowset(["noteID", "label"], [
                new PyList([Id(1), new PyStr("S:Folders")]),
                new PyList([Id(2), new PyStr("Briefing")]),
            ]),
            await Ask(first, "GetOwnerNoteLabels"));
        Assert.Equal(
            new PyList([Py.KeyVal(("noteID", Id(2)), ("label", new PyStr("Briefing")), ("note", new PyStr("Dock at Perimeter")))]),
            await Ask(first, "GetOwnerNote", Id(2)));
        Assert.Equal(new PyStr("Saved ship note"), await Ask(first, "GetNote", Id(4001)));

        Assert.Equal(
            Py.KeyVal(
                ("addresses", Py.Rowset(
                    ["contactID", "inWatchlist", "relationshipID", "labelMask"],
                    [new PyList([Id(2002), Id(0), Id(-5), Id(0)])])),
                ("blocked", Py.Rowset(["senderID"], []))),
            await Ask(second, "GetContactList"));
        Assert.Equal(
            new PyDict([new KeyValuePair<PyValue, PyValue>(
                Id(8), Py.KeyVal(("labelID", Id(8)), ("name", new PyStr("Friends")), ("color", Id(3))))]),
            await Ask(second, "GetLabels"));
        Assert.Equal(
            Py.Rowset(["noteID", "label"], [
                new PyList([Id(4), new PyStr("Personal")]),
                new PyList([Id(5), new PyStr("S:Folders")]),
            ]),
            await Ask(second, "GetOwnerNoteLabels"));
        Assert.Equal(
            new PyList([Py.KeyVal(
                ("noteID", Id(5)),
                ("label", new PyStr("S:Folders")),
                ("note", new PyStr("1::F::0::Main|")))]),
            await Ask(second, "GetOwnerNote", Id(5)));
        Assert.Equal(new PyStr(""), await Ask(second, "GetNote", Id(4001)));
    }

    [PgFact]
    public async Task Social_and_note_reads_keep_the_Evejs_empty_contract()
    {
        await using var rig = await Rig.CreateAsync();
        var id = await rig.MakeAsync("Ann Lee");
        var (session, _) = Rig.Session(id);
        Task<PyValue> Ask(string method, params PyValue[] args) => Rig.CallAsync(rig.CharMgr, session, method, args);

        Assert.Equal(new PyDict([]), await Ask("GetLabels"));
        Assert.Equal(
            Py.Rowset(["noteID", "label"], [new PyList([Id(1), new PyStr("S:Folders")])]),
            await Ask("GetOwnerNoteLabels"));
        Assert.Equal(
            new PyList([Py.KeyVal(
                ("noteID", Id(1)),
                ("label", new PyStr("S:Folders")),
                ("note", new PyStr("1::F::0::Main|")))]),
            await Ask("GetOwnerNote", Id(1)));
        Assert.Equal(
            new PyList([Py.KeyVal(
                ("noteID", Id(999)),
                ("label", new PyStr("")),
                ("note", new PyStr("")))]),
            await Ask("GetOwnerNote", Id(999)));
        Assert.Equal(new PyStr(""), await Ask("GetNote", Id(123)));

        var (unbound, _) = Rig.Session(null);
        Assert.Equal(
            Py.Rowset(["noteID", "label"], []),
            await Rig.CallAsync(rig.CharMgr, unbound, "GetOwnerNoteLabels"));
    }


}

public class AccountTests
{
    [PgFact]
    public async Task The_cash_balance_is_the_characters_isk_as_a_real_number()
    {
        await using var rig = await Rig.CreateAsync();
        var id = await rig.MakeAsync("Ann Lee", isk: 1234.56m);
        var (session, _) = Rig.Session(id);
        Assert.Equal(new PyReal(1234.56), await Rig.CallAsync(rig.Account, session, "GetCashBalance"));
        Assert.Equal(new PyReal(1234.56), await Rig.CallAsync(rig.Account, session, "GetCashBalance", new PyInt(0)));
        Assert.Equal(new PyReal(1234.56), await Rig.CallAsync(rig.Account, session, "GetCashBalance", PyBool.False));
    }

    [PgFact]
    public async Task A_corporation_wallet_is_refused_with_the_recorded_reason()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _) = Rig.Session(await rig.MakeAsync("Ann Lee"));
        var error = (await Assert.ThrowsAsync<MachoWrappedException>(async () => await Rig.CallAsync(rig.Account, session, "GetCashBalance", new PyInt(1)))).Error;

        Assert.Equal(2, error.ErrorCode);
        Assert.True(error.IsUserError);
        Assert.Equal("CrpAccessDenied", error.UserErrorMessage);
        var state = (PyDict)((PyTuple)((PyObjectEx)error.Payload).Header).Items[2];
        var reason = (PyDict)((PyTuple)((PyTuple)((PyObjectEx)error.Payload).Header).Items[1]).Items[1];
        Assert.Equal(new PyTuple([new PyInt(101), new PyStr("UI/Corporations/AccessRestrictions/NoAccessToWalletDivision")]), reason.Entries.Single().Value);
        Assert.Equal("reason", JsSemantics.StringValue(reason.Entries.Single().Key));
        Assert.Equal(2, state.Entries.Count);
    }

    [PgFact]
    public async Task Without_a_character_or_a_wallet_there_is_no_balance()
    {
        await using var rig = await Rig.CreateAsync();
        var (nobody, _) = Rig.Session(null);
        Assert.Equal(PyNone.Instance, await Rig.CallAsync(rig.Account, nobody, "GetCashBalance"));
        var (stranger, _) = Rig.Session(140_999_999);
        Assert.Equal(PyNone.Instance, await Rig.CallAsync(rig.Account, stranger, "GetCashBalance"));
    }
}
