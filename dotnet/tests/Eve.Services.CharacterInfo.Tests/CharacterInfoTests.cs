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

    public CharMgrService CreateCharMgr(
    Func<long, CancellationToken, ValueTask<CharMgrPersonalData?>>? readPersonalData = null,
    Func<long, Func<CharMgrPersonalData, CharMgrPersonalData>, CancellationToken, ValueTask<CharMgrPersonalData?>>? updatePersonalData = null,
    Func<long, long, string?, CancellationToken, ValueTask>? writeActivityStatus = null,
    Func<long, PyValue, CancellationToken, ValueTask>? writeLogSettings = null,
    Func<long, CancellationToken, ValueTask<long?>>? readCorporationMemberStartedMs = null,
    Func<IReadOnlyList<Character>, CancellationToken, ValueTask<IReadOnlyDictionary<long, CharMgrOrganizationInfo>>>? readOrganizationInfo = null,
    HomeStationResolver? homeStations = null) =>
    new(Characters, homeStations ?? CreateHomeStationResolver(Characters), ReadRecentShipKillmailPageAsync,
        ReadCharacterDescriptionAsync, WriteCharacterDescriptionAsync, ReadSecurityStatusAsync, ReadMedal1GraphicIdAsync,
        readPersonalData, updatePersonalData, writeActivityStatus, writeLogSettings,
        readCorporationMemberStartedMs, readOrganizationInfo,
        readBalance: async (characterId, token) => (await Wallets.GetAsync(characterId, token))?.Isk);

public static HomeStationResolver CreateHomeStationResolver(
    PgCharacterStore characters,
    Func<long, CancellationToken, Task<HomeStationStructure?>>? readStructure = null,
    Func<Character, CancellationToken, Task<IReadOnlyList<HomeStationLocation>>>? readCorporationOfficeCandidates = null)
{
    var starter = Stations.Get(60015249)!;
    var catalog = new CreationCatalog([], [], [
        new StarterLocation(1000006, null, starter.Id, starter.SolarSystemId, starter.ConstellationId, starter.RegionId),
    ]);
    return new HomeStationResolver(
        (id, token) => characters.GetAsync(id, token),
        Stations,
        catalog,
        readStructure ?? ((_, _) => Task.FromResult<HomeStationStructure?>(null)),
        readCorporationOfficeCandidates);
}

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
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url!));
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
    public async Task Home_station_and_character_asset_resolution_use_persisted_character_state()
    {
        await using var rig = await Rig.CreateAsync();
        var id = await rig.MakeAsync("Home Station Pilot");
        var (session, _) = Rig.Session(id);
        var homeStation = new HomeStationService(Rig.CreateHomeStationResolver(rig.Characters));

        var response = Assert.IsType<PyObjectEx>(await Rig.CallAsync(homeStation, session, "get_home_station"));
        Assert.Equal(ObjectExKind.Ex2, response.Kind);
        var header = Assert.IsType<PyTuple>(response.Header);
        Assert.Equal(new PyToken("homestation.types.StationData"),
            Assert.Single(Assert.IsType<PyTuple>(header.Items[0]).Items));
        var fields = Assert.IsType<PyDict>(header.Items[1]).Entries
            .ToDictionary(entry => Assert.IsType<PyStr>(entry.Key).Value, entry => entry.Value);
        Assert.Equal(PyBool.False, fields["is_fallback"]);
        Assert.Equal(new PyInt(60015249), fields["id"]);
        Assert.Equal(new PyInt(92885), fields["type_id"]);
        Assert.Equal(new PyInt(30100032), fields["solar_system_id"]);

        var charMgr = rig.CreateCharMgr();
        var nodeId = await Rig.CallAsync(charMgr, session, "MachoResolveObject",
            Py.Tuple(new PyInt(id), new PyInt(10002)));
        Assert.Equal(new PyInt(Eve.Macho.MachoMessage.ProxyNodeId), nodeId);
    }

    [PgFact]
    public async Task Without_a_character_or_a_known_station_there_is_no_home_station_row()
    {
        await using var rig = await Rig.CreateAsync();
        var (nobody, _) = Rig.Session(null);
        Assert.Equal(PyNone.Instance, await Rig.CallAsync(rig.CharMgr, nobody, "GetHomeStationRow"));

        var lost = await rig.MakeAsync("Lost Pilot", station: 60099999);
        var (session, _) = Rig.Session(lost);
        var unavailable = new HomeStationResolver(
            (id, token) => rig.Characters.GetAsync(id, token), Rig.Stations,
            new CreationCatalog([], [], []), (_, _) => Task.FromResult<HomeStationStructure?>(null));
        Assert.Equal(PyNone.Instance, await Rig.CallAsync(rig.CreateCharMgr(homeStations: unavailable), session, "GetHomeStationRow"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Rig.CallAsync(new HomeStationService(unavailable), session, "get_home_station"));
    }

    private static Dictionary<string, PyValue> ObjectFields(PyValue value)
    {
        var header = Assert.IsType<PyTuple>(Assert.IsType<PyObjectEx>(value).Header);
        return Assert.IsType<PyDict>(header.Items[1]).Entries
            .ToDictionary(entry => Assert.IsType<PyStr>(entry.Key).Value, entry => entry.Value);
    }

    [PgFact]
    public async Task Active_structure_home_station_is_shared_by_homeStation_and_charMgr_reads()
    {
        const long structureId = 1_000_000_000_011;
        await using var rig = await Rig.CreateAsync();
        var npc = Rig.Stations.Get(60015249)!;
        var id = await rig.MakeAsync("Structure Home Pilot", station: structureId);
        var (session, _) = Rig.Session(id);
        var structure = new HomeStationStructure(
            new HomeStationLocation(structureId, 35841, "Pilot Citadel", npc.SolarSystemId, npc.ConstellationId, npc.RegionId, 980001, false),
            PersistentlyDeployed: true, IsLost: false);
        var resolver = Rig.CreateHomeStationResolver(rig.Characters,
            (requestedId, _) => Task.FromResult<HomeStationStructure?>(requestedId == structureId ? structure : null));

        var home = await Rig.CallAsync(new HomeStationService(resolver), session, "get_home_station");
        var homeFields = ObjectFields(home);
        Assert.Equal(new PyInt(structureId), homeFields["id"]);
        Assert.Equal(new PyInt(35841), homeFields["type_id"]);
        Assert.Equal(new PyInt(npc.SolarSystemId), homeFields["solar_system_id"]);
        Assert.Equal(PyBool.False, homeFields["is_fallback"]);

        var charMgr = rig.CreateCharMgr(homeStations: resolver);
        var row = await Rig.CallAsync(charMgr, session, "GetHomeStationRow");
        var rowFields = Fields(row);
        Assert.Equal(new PyStr("Pilot Citadel"), rowFields["stationName"]);
        Assert.Equal(new PyInt(structureId), rowFields["stationID"]);
        Assert.Equal(new PyInt(npc.ConstellationId), rowFields["constellationID"]);
        Assert.Equal(new PyInt(npc.RegionId), rowFields["regionID"]);
        Assert.Equal(new PyInt(980001), rowFields["ownerID"]);
        Assert.Equal(PyBool.False, rowFields["isFallback"]);

        var alias = await Rig.CallAsync(charMgr, session, "GetHomeStation");
        Assert.Equal(Order(row), Order(alias));
        Assert.Equal(Fields(row), Fields(alias));
    }

    [PgFact]
    public async Task Lost_structure_home_station_falls_back_to_the_school_station_on_both_read_surfaces()
    {
        const long structureId = 1_000_000_000_012;
        await using var rig = await Rig.CreateAsync();
        var starter = Rig.Stations.Get(60015249)!;
        var id = await rig.MakeAsync("Lost Structure Pilot", station: structureId);
        var (session, _) = Rig.Session(id);
        foreach (var lost in new HomeStationStructure?[]
        {
            null,
            new(null, PersistentlyDeployed: false, IsLost: true),
        })
        {
            var resolver = Rig.CreateHomeStationResolver(rig.Characters,
                (requestedId, _) => Task.FromResult<HomeStationStructure?>(requestedId == structureId ? lost : null));

            var home = await Rig.CallAsync(new HomeStationService(resolver), session, "get_home_station");
            var homeFields = ObjectFields(home);
            Assert.Equal(new PyInt(starter.Id), homeFields["id"]);
            Assert.Equal(PyBool.True, homeFields["is_fallback"]);

            var row = await Rig.CallAsync(rig.CreateCharMgr(homeStations: resolver), session, "GetHomeStationRow");
            var fields = Fields(row);
            Assert.Equal(new PyInt(starter.Id), fields["stationID"]);
            Assert.Equal(new PyInt(starter.SolarSystemId), fields["solarSystemID"]);
            Assert.Equal(PyBool.True, fields["isFallback"]);
        }
    }

    [PgFact]
    public async Task The_paperdoll_state_follows_the_stored_appearance_of_the_character()
    {
        await using var rig = await Rig.CreateAsync();
        var id = await rig.MakeAsync("Doll Ann");
        var (session, _) = Rig.Session(id);
        var (noCharacter, _) = Rig.Session(null);

        Assert.Equal(new PyInt(2), await Rig.CallAsync(rig.CharMgr, session, "GetPaperdollState"));
        Assert.Equal(new PyInt(2), await Rig.CallAsync(rig.CharMgr, noCharacter, "GetPaperdollState"));

        await rig.Characters.SetAppearanceAsync(id, MarshalCodec.Encode(new PyDict([new(new PyStr("recorded"), new PyInt(1))])), true, null, false);

        Assert.Equal(new PyInt(0), await Rig.CallAsync(rig.CharMgr, session, "GetPaperdollState"));
        Assert.Equal(new PyInt(2), await Rig.CallAsync(rig.CharMgr, noCharacter, "GetPaperdollState"));
    }

    [PgFact]
    public async Task Paperdoll_state_ignores_a_client_supplied_other_character_id()
    {
        await using var rig = await Rig.CreateAsync();
        var ownId = await rig.MakeAsync("Doll Ann");
        var otherId = await rig.MakeAsync("Doll Bob");
        await rig.Characters.SetAppearanceAsync(otherId,
            MarshalCodec.Encode(new PyDict([new(new PyStr("recorded"), new PyInt(1))])), true, null, false);
        var (session, _) = Rig.Session(ownId);

        Assert.Equal(new PyInt(2), await Rig.CallAsync(rig.CharMgr, session, "GetPaperdollState", Id(otherId)));
    }

    [PgFact]
    public async Task The_fixed_answers_are_the_recorded_ones()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _) = Rig.Session(await rig.MakeAsync("Ann Lee"));
        Task<PyValue> Ask(string method, params PyValue[] args) => Rig.CallAsync(rig.CharMgr, session, method, args);

        // a character made here has no stored doll: the reference answers "no existing customization" (2) for it
        Assert.Equal(Id(2), await Ask("GetPaperdollState"));
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
    public async Task Private_info_returns_the_authoritative_member_editor_row()
    {
        await using var rig = await Rig.CreateAsync();
        var characterId = await rig.MakeAsync("Ann Lee", isk: 54321.75m);
        rig.SecurityStatuses[characterId] = -1.25;
        var character = (await rig.Characters.GetAsync(characterId))!;
        var (session, _) = Rig.Session(characterId);

        var actual = await Rig.CallAsync(rig.CharMgr, session, "GetPrivateInfo", Id(characterId));
        var expected = Py.Rowset(
            ["characterID", "gender", "createDateTime", "raceID", "bloodlineID", "ancestryID", "balance", "securityRating"],
            [new PyList([
                Id(characterId), new PyInt(character.Gender), await Rig.CallAsync(rig.CharMgr, session, "GetCharacterCreationDate", Id(characterId)),
                new PyInt(character.RaceId), new PyInt(character.BloodlineId), new PyInt(character.AncestryId),
                Py.Number(54321.75), Py.Number(-1.25),
            ])]);
        Assert.True(PyValue.DeepEquals(expected, actual));
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
    public async Task Personal_contact_labels_can_be_created_edited_assigned_removed_and_deleted()
    {
        await using var rig = await Rig.CreateAsync();
        var characterId = await rig.MakeAsync("Ann Lee");
        var state = new CharMgrPersonalData(
            [new CharMgrContact(2001, true, -10, 1)], [],
            new Dictionary<long, CharMgrContactLabel> { [1] = new("Fleet", 7) }, [],
            new Dictionary<long, string>())
        { NextContactLabelId = 3 };
        var service = rig.CreateCharMgr(
            (ownerId, _) => ValueTask.FromResult<CharMgrPersonalData?>(ownerId == characterId ? state : null),
            (_, update, _) =>
            {
                state = update(state);
                return ValueTask.FromResult<CharMgrPersonalData?>(state);
            });
        var (session, _) = Rig.Session(characterId);
        async Task<PyValue> Call(string method, params PyValue[] args) => await Rig.CallAsync(service, session, method, args);

        Assert.Equal(Id(4), await Call("CreateLabel", new PyStr("Wing"), Id(5)));
        Assert.Equal(new CharMgrContact(2001, true, -10, 1), Assert.Single(state.Contacts));
        Assert.IsType<PyNone>(await Call("AssignLabels", new PyList([Id(2001), Id(2002)]), new PyLong(4)));
        Assert.Equal(new[] { new CharMgrContact(2001, true, -10, 5), new CharMgrContact(2002, false, 0, 4) },
            state.Contacts.OrderBy(contact => contact.ContactId));
        Assert.IsType<PyNone>(await Call("EditLabel", new PyLong(1), new PyStr("Fleet pilots"), Id(9)));
        Assert.IsType<PyNone>(await Call("RemoveLabels", new PyList([Id(2001)]), new PyLong(1)));
        Assert.IsType<PyNone>(await Call("DeleteLabel", new PyLong(4)));
        Assert.Equal(Id(8), await Call("CreateLabel", new PyStr("Next"), Id(6)));
        Assert.Equal(new Dictionary<long, CharMgrContactLabel>
        {
            [1] = new("Fleet pilots", 9), [8] = new("Next", 6),
        }, state.Labels);
        Assert.Equal(new[] { new CharMgrContact(2001, true, -10, 0), new CharMgrContact(2002, false, 0, 0) },
            state.Contacts.OrderBy(contact => contact.ContactId));
    }


    [PgFact]
    public async Task EditContact_updates_relationship_and_watchlist_through_the_client_method()
    {
        await using var rig = await Rig.CreateAsync();
        var characterId = await rig.MakeAsync("Ann Lee");
        var state = new CharMgrPersonalData(
            [new CharMgrContact(2001, false, 0, 3)], [],
            new Dictionary<long, CharMgrContactLabel> { [1] = new("Fleet", 7) }, [],
            new Dictionary<long, string>());
        var service = rig.CreateCharMgr(
            (ownerId, _) => ValueTask.FromResult<CharMgrPersonalData?>(ownerId == characterId ? state : null),
            (_, update, _) =>
            {
                state = update(state);
                return ValueTask.FromResult<CharMgrPersonalData?>(state);
            });
        var (session, _) = Rig.Session(characterId);
        var result = await Rig.CallAsync(service, session, "EditContact", [Id(2001), Id(-10), PyBool.True]);
        Assert.IsType<PyNone>(result);
        Assert.Equal(new CharMgrContact(2001, true, -10, 3), Assert.Single(state.Contacts));
    }


    [PgFact]
    public async Task Get_private_info_on_corporation_change_returns_the_reference_keyval_shape()
    {
        await using var rig = await Rig.CreateAsync();
        var characterId = await rig.MakeAsync("Change Corp");
        var character = await rig.Characters.GetAsync(characterId);
        var service = rig.CreateCharMgr();
        var (session, _) = Rig.Session(characterId);

        Assert.Equal(
            Py.KeyVal(
                ("corporationID", Id(1000006)),
                ("corporationDateTime", new PyLong(Py.FileTimeFromUnixMs(character!.CreatedAt.ToUnixTimeMilliseconds())))),
            await Rig.CallAsync(service, session, "GetPrivateInfoOnCorpChange", Id(characterId)));
    }


    [PgFact]
    public async Task Private_corporation_change_info_ignores_a_client_supplied_other_character_id()
    {
        await using var rig = await Rig.CreateAsync();
        var ownId = await rig.MakeAsync("Own Corp Change");
        var otherId = await rig.MakeAsync("Other Corp Change");
        long? readCharacterId = null;
        var service = rig.CreateCharMgr(readCorporationMemberStartedMs: (id, _) =>
        {
            readCharacterId = id;
            return ValueTask.FromResult<long?>(1_700_000_000_123);
        });
        var (session, _) = Rig.Session(ownId);

        await Rig.CallAsync(service, session, "GetPrivateInfoOnCorpChange", Id(otherId));

        Assert.Equal(ownId, readCharacterId);
    }

    [PgFact]
    public async Task Personal_char_mgr_rpcs_preserve_client_shapes_and_update_owner_scoped_state()
    {
        await using var rig = await Rig.CreateAsync();
        var characterId = await rig.MakeAsync("Personal RPCs");
        var store = new Dictionary<long, CharMgrPersonalData>
        {
            [characterId] = new(
                [new CharMgrContact(2001, false, 0, 9)],
                [3000],
                new Dictionary<long, CharMgrContactLabel>(),
                [new CharMgrOwnerNote(1, "S:Folders", "1::F::0::Main|"),
                    new CharMgrOwnerNote(2, "Briefing", "old")],
                new Dictionary<long, string> { [4001] = "old item note" })
            {
                NextOwnerNoteId = 5,
            },
        };
        (long CharacterId, long Status, string? ExtraInfo)? activity = null;
        PyValue? loggedSettings = null;
        var settings = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("window"), new PyStr("chat")),
        ]);
        var service = rig.CreateCharMgr(
            (id, _) => ValueTask.FromResult(store.GetValueOrDefault(id)),
            (id, update, _) =>
            {
                var updated = update(store.GetValueOrDefault(id) ?? CharMgrPersonalData.Empty);
                store[id] = updated;
                return ValueTask.FromResult<CharMgrPersonalData?>(updated);
            },
            (id, status, extraInfo, _) =>
            {
                activity = (id, status, extraInfo);
                return ValueTask.CompletedTask;
            },
            (_, value, _) =>
            {
                loggedSettings = value;
                return ValueTask.CompletedTask;
            },
            (_, _) => ValueTask.FromResult<long?>(1_700_000_000_123));
        var (session, _) = Rig.Session(characterId);

        Assert.Equal(
            Py.KeyVal(
                ("corporationID", Id(1000006)),
                ("corporationDateTime", new PyLong(Py.FileTimeFromUnixMs(1_700_000_000_123)))),
            await Rig.CallAsync(service, session, "GetPrivateInfoOnCorpChange", Id(characterId)));

        Assert.Equal(PyNone.Instance, await Rig.CallAsync(service, session, "AddContact", Id(2002), Id(-10), PyBool.True));
        Assert.Equal(PyNone.Instance, await Rig.CallAsync(service, session, "EditContactsRelationshipID",
            new PyList([Id(2001), Id(2002)]), Id(-5)));
        Assert.Equal(PyNone.Instance, await Rig.CallAsync(service, session, "BlockOwners", new PyList([Id(3001), Id(3002)])));
        Assert.Equal(PyNone.Instance, await Rig.CallAsync(service, session, "UnblockOwners", new PyList([Id(3001)])));
        Assert.Equal(PyNone.Instance, await Rig.CallAsync(service, session, "DeleteContacts", new PyList([Id(2002)])));

        var list = await Rig.CallAsync(service, session, "GetContactList");
        Assert.Equal(
            Py.KeyVal(
                ("addresses", Py.Rowset(["contactID", "inWatchlist", "relationshipID", "labelMask"],
                    [new PyList([Id(2001), Id(0), Id(-5), Id(9)])])),
                ("blocked", Py.Rowset(["senderID"], [new PyList([Id(3000)]), new PyList([Id(3002)])]))),
            list);

        Assert.Equal(Id(5), await Rig.CallAsync(service, session, "AddOwnerNote", new PyStr("Status"), new PyStr("ready")));
        Assert.Equal(PyNone.Instance, await Rig.CallAsync(service, session, "EditOwnerNote", Id(5), new PyStr("Updated")));
        Assert.Equal(
            new PyList([Py.KeyVal(("noteID", Id(5)), ("label", new PyStr("Updated")), ("note", new PyStr("ready")))]),
            await Rig.CallAsync(service, session, "GetOwnerNote", Id(5)));
        Assert.Equal(PyNone.Instance, await Rig.CallAsync(service, session, "RemoveOwnerNote", Id(5)));
        Assert.Equal(PyNone.Instance, await Rig.CallAsync(service, session, "SetNote", Id(4002), new PyStr("saved")));
        Assert.Equal(new PyStr("saved"), await Rig.CallAsync(service, session, "GetNote", Id(4002)));

        Assert.Equal(PyNone.Instance, await ((IService)service).CallAsync(new ServiceCall
        {
            Method = "SetActivityStatus",
            Args = [],
            Kwargs = new PyDict([
                new KeyValuePair<PyValue, PyValue>(new PyStr("status"), Id(2)),
                new KeyValuePair<PyValue, PyValue>(new PyStr("extraInfo"), new PyLong(12345)),
            ]),
            Session = session,
        }));
        Assert.Equal((characterId, 2, "12345"), activity);

        Assert.Equal(PyNone.Instance, await Rig.CallAsync(service, session, "LogSettings", settings));
        Assert.Equal(settings, loggedSettings);

        Assert.Equal(new PyDict([]), await Rig.CallAsync(service, session, "GetCharacterSettings"));
        Assert.Equal(PyNone.Instance, await Rig.CallAsync(service, session, "SaveCharacterSetting", new PyStr("window"), new PyStr("dock")));
        Assert.Equal(PyNone.Instance, await Rig.CallAsync(service, session, "SaveCharacterSetting", new PyStr("fleetSetups"), new PyStr("[]")));
        var savedSettings = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("fleetSetups"), new PyStr("[]")),
            new KeyValuePair<PyValue, PyValue>(new PyStr("window"), new PyStr("dock")),
        ]);
        Assert.Equal(savedSettings, await Rig.CallAsync(service, session, "GetCharacterSettings"));
        await Assert.ThrowsAsync<Eve.Services.MachoWrappedException>(() => Rig.CallAsync(
            service, session, "SaveCharacterSetting", new PyStr("large"), new PyStr(new string('x', 102401))));
        Assert.Equal(savedSettings, await Rig.CallAsync(service, session, "GetCharacterSettings"));
        Assert.Equal(PyNone.Instance, await Rig.CallAsync(service, session, "DeleteCharacterSetting", new PyStr("window")));
        Assert.Equal(
            new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("fleetSetups"), new PyStr("[]"))]),
            await Rig.CallAsync(service, session, "GetCharacterSettings"));
        Assert.Equal(PyNone.Instance, await Rig.CallAsync(service, session, "DeleteCharacterSetting", new PyStr("fleetSetups")));
        Assert.Equal(new PyDict([]), await Rig.CallAsync(service, session, "GetCharacterSettings"));
    }


    [PgFact]
    public async Task Get_organization_info_for_local_chat_returns_keyed_keyvals_from_the_existing_stores()
    {
        await using var rig = await Rig.CreateAsync();
        var caldari = await rig.MakeAsync("Chat Caldari", corporation: 1000006, faction: 500001);
        var amarr = await rig.MakeAsync("Chat Amarr", corporation: 1000009, faction: 500003);
        IReadOnlyList<long> loadedCharacters = [];
        var service = rig.CreateCharMgr(readOrganizationInfo: (characters, _) =>
        {
            loadedCharacters = characters.Select(character => character.Id).ToArray();
            IReadOnlyDictionary<long, CharMgrOrganizationInfo> affiliations = characters.ToDictionary(
                character => character.Id,
                character => character.Id == caldari
                    ? new CharMgrOrganizationInfo(character.CorporationId, 99000001, 500001)
                    : new CharMgrOrganizationInfo(character.CorporationId, 0, 0));
            return ValueTask.FromResult(affiliations);
        });
        var (session, _) = Rig.Session(caldari);

        var result = await Rig.CallAsync(
            service,
            session,
            "GetOrganizationInfoForCharacters",
            new PyList([Id(amarr), Id(caldari), Id(0), new PyStr("invalid"), Id(999999999)]));

        Assert.Equal(new long[] { caldari, amarr }, loadedCharacters.Order().ToArray());
        Assert.Equal(
            new PyDict([
                new KeyValuePair<PyValue, PyValue>(Id(amarr),
                    Py.KeyVal(
                        ("corporationID", Id(1000009)),
                        ("allianceID", Id(0)),
                        ("warFactionID", Id(0)))),
                new KeyValuePair<PyValue, PyValue>(Id(caldari),
                    Py.KeyVal(
                        ("corporationID", Id(1000006)),
                        ("allianceID", Id(99000001)),
                        ("warFactionID", Id(500001)))),
                new KeyValuePair<PyValue, PyValue>(Id(999999999),
                    Py.KeyVal(
                        ("corporationID", Id(0)),
                        ("allianceID", Id(0)),
                        ("warFactionID", Id(0)))),
            ]),
            result);
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

    [PgFact]
    public async Task Home_station_candidates_list_school_offices_and_current_with_client_validation_flags()
    {
        await using var rig = await Rig.CreateAsync();
        var id = await rig.MakeAsync("Home Candidate Pilot");
        var (session, _) = Rig.Session(id);
        session.Set("stationid", new PyInt(60015249));

        var office = Rig.Stations.Get(60000610)!;
        var officeLocation = new HomeStationLocation(office.Id, office.TypeId, office.Name, office.SolarSystemId,
            office.ConstellationId, office.RegionId, office.OwnerId, false);
        var offices = new HomeStationLocation[]
        {
            officeLocation,
            officeLocation,
            new(60001000, 4023, "Wormhole Office", 31_000_001, 2_000_001, 11_000_001, 1_000_006, false),
            new(60001001, 4023, "Thera Office", 31_000_005, 2_000_001, 11_000_031, 1_000_006, false),
        };
        var resolver = Rig.CreateHomeStationResolver(rig.Characters,
            readCorporationOfficeCandidates: (_, _) => Task.FromResult<IReadOnlyList<HomeStationLocation>>(offices));
        var candidateNow = new DateTime(2026, 10, 11, 12, 34, 56, DateTimeKind.Utc).ToFileTimeUtc();
        var service = new HomeStationService(resolver, nowFileTime: () => candidateNow);

        var candidates = Assert.IsType<PyList>(await Rig.CallAsync(service, session, "get_home_station_candidates"));
        Assert.Equal(4, candidates.Items.Count);

        var school = ObjectFields(candidates.Items[0]);
        Assert.Equal(new PyInt(60015249), school["id"]);
        Assert.Equal(PyBool.True, school["is_current_station"]);
        Assert.Equal(PyBool.True, school["is_school_hq"]);
        Assert.Equal(new PyList([new PyInt(2)]), school["errors"]);

        var officeFields = ObjectFields(candidates.Items[1]);
        Assert.Equal(new PyInt(60000610), officeFields["id"]);
        Assert.Equal(PyBool.False, officeFields["is_current_station"]);
        Assert.Equal(PyBool.False, officeFields["is_school_hq"]);
        Assert.Equal(new PyList([]), officeFields["errors"]);

        Assert.Equal(new PyList([new PyInt(1)]), ObjectFields(candidates.Items[2])["errors"]);
        Assert.Equal(new PyList([]), ObjectFields(candidates.Items[3])["errors"]);
        Assert.Equal(PyNone.Instance, await Rig.CallAsync(service, session, "get_next_remote_change_time"));
    }

    [PgFact]
    public async Task Home_station_candidates_include_the_current_structure_without_a_corporation_office()
    {
        await using var rig = await Rig.CreateAsync();
        var id = await rig.MakeAsync("Structure Home Candidate");
        var (session, _) = Rig.Session(id);
        const long structureId = 1_000_000_000_051;
        var location = new HomeStationLocation(structureId, 35841, "Current Structure", 30_002_775,
            2_000_407, 10_000_033, 98_000_051, false);
        var resolver = Rig.CreateHomeStationResolver(rig.Characters,
            (candidateId, _) => Task.FromResult<HomeStationStructure?>(candidateId == structureId
                ? new(location, true, false) : null));
        session.Set("stationid", PyNone.Instance);
        session.Set("structureid", new PyInt(structureId));
        session.Set("shipid", new PyInt(1_000_000_000_052));
        var service = new HomeStationService(resolver, rig.Characters.TryChangeHomeStationAsync,
            rig.Characters.ResetRemoteHomeStationChangeTimeAsync,
            () => new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc());

        var candidates = Assert.IsType<PyList>(await Rig.CallAsync(service, session, "get_home_station_candidates"));
        var current = Assert.Single(candidates.Items, candidate => ObjectFields(candidate)["id"].Equals(new PyInt(structureId)));
        Assert.Equal(PyBool.True, ObjectFields(current)["is_current_station"]);
        Assert.Equal(new PyList([]), ObjectFields(current)["errors"]);
        Assert.Equal(PyNone.Instance, await Rig.CallAsync(service, session, "set_home_station", new PyInt(structureId), PyBool.False));
        Assert.Equal(structureId, (await rig.Characters.GetAsync(id))!.HomeStationId);

        session.Set("shipid", new PyInt(structureId));
        candidates = Assert.IsType<PyList>(await Rig.CallAsync(service, session, "get_home_station_candidates"));
        Assert.DoesNotContain(candidates.Items, candidate => ObjectFields(candidate)["id"].Equals(new PyInt(structureId)));
    }

    [PgFact]
    public async Task Home_station_remote_change_persists_notifies_serializes_datetime_and_resets()
    {
        await using var rig = await Rig.CreateAsync();
        var id = await rig.MakeAsync("Remote Home Pilot");
        var sink = new RecordingPacketSink();
        var session = new ClientSession(sink) { SessionId = 1, ClientId = 2, UserId = 34 };
        session.CharacterId = id;
        session.Set("stationid", new PyInt(60015249));

        var office = Rig.Stations.Get(60000610)!;
        var officeLocation = new HomeStationLocation(office.Id, office.TypeId, office.Name, office.SolarSystemId,
            office.ConstellationId, office.RegionId, office.OwnerId, false);
        var anotherOffice = new HomeStationLocation(60001000, 4023, "Remote Office", 30_002_775,
            2_000_407, 10_000_033, 1_000_006, false);
        var resolver = Rig.CreateHomeStationResolver(rig.Characters,
            readCorporationOfficeCandidates: (_, _) => Task.FromResult<IReadOnlyList<HomeStationLocation>>([officeLocation, anotherOffice]));
        var now = new DateTime(2026, 10, 11, 12, 34, 56, 789, DateTimeKind.Utc).ToFileTimeUtc();
        var service = new HomeStationService(resolver, rig.Characters.TryChangeHomeStationAsync,
            rig.Characters.ResetRemoteHomeStationChangeTimeAsync, () => now);

        Assert.Equal(PyNone.Instance, await Rig.CallAsync(service, session, "set_home_station", new PyInt(60000610), PyBool.True));

        var expectedNext = now + TimeSpan.FromDays(365).Ticks;
        var persisted = Assert.IsType<Character>(await rig.Characters.GetAsync(id, CancellationToken.None));
        Assert.Equal(60000610, persisted.HomeStationId);
        Assert.Equal(expectedNext, persisted.NextRemoteHomeStationChangeFileTime);
        Assert.Equal(new PyInt(60000610), ObjectFields(await Rig.CallAsync(service, session, "get_home_station"))["id"]);
        foreach (var name in new[] { "homeStationID", "homestationid", "cloneStationID", "clonestationid" })
            Assert.Equal(60000610, session.GetInt(name));

        Assert.Equal(MarshalCodec.Encode(Eve.Services.ClientNotification.Build(session.UserId ?? 0, session.NodeId,
            "OnHomeStationChanged", "charid", [new PyInt(60000610)], 0)), MarshalCodec.Encode(Assert.Single(sink.Packets)));

        var next = Assert.IsType<PyCPickled>(await Rig.CallAsync(service, session, "get_next_remote_change_time"));
        Assert.Equal(
            "8002636461746574696D650A6461746574696D650A7100635F636F646563730A656E636F64650A7101580B00000007C3AB0A0B0C22380C0A08710258060000006C6174696E3171038671045271058571065271072E",
            Convert.ToHexString(next.Value.Span));

        var candidates = Assert.IsType<PyList>(await Rig.CallAsync(service, session, "get_home_station_candidates"));
        var remote = candidates.Items.Single(item => ObjectFields(item)["id"].Equals(new PyInt(60001000)));
        Assert.Equal(new PyList([new PyInt(3)]), ObjectFields(remote)["errors"]);
        await AssertHomeStationValidationAsync(
            async () => { await Rig.CallAsync(service, session, "set_home_station", new PyInt(60001000), PyBool.True); }, 3);
        Assert.Single(sink.Packets);

        var expiredService = new HomeStationService(resolver, rig.Characters.TryChangeHomeStationAsync,
            rig.Characters.ResetRemoteHomeStationChangeTimeAsync, () => expectedNext + 1);
        Assert.Equal(PyNone.Instance, await Rig.CallAsync(expiredService, session, "get_next_remote_change_time"));

        Assert.Equal(PyNone.Instance, await Rig.CallAsync(service, session, "reset_remote_change_time"));
        persisted = Assert.IsType<Character>(await rig.Characters.GetAsync(id, CancellationToken.None));
        Assert.Equal(0, persisted.NextRemoteHomeStationChangeFileTime);
        Assert.Equal(PyNone.Instance, await Rig.CallAsync(service, session, "get_next_remote_change_time"));

        Assert.Equal(PyNone.Instance, await Rig.CallAsync(service, session, "set_home_station", new PyInt(60015249), PyBool.False));
        persisted = Assert.IsType<Character>(await rig.Characters.GetAsync(id, CancellationToken.None));
        Assert.Equal(60015249, persisted.HomeStationId);
        Assert.Equal(0, persisted.NextRemoteHomeStationChangeFileTime);
        Assert.Equal(2, sink.Packets.Count);
        Assert.Equal(MarshalCodec.Encode(Eve.Services.ClientNotification.Build(session.UserId ?? 0, session.NodeId,
            "OnHomeStationChanged", "charid", [new PyInt(60015249)], 1)), MarshalCodec.Encode(sink.Packets[1]));
    }

    [PgFact]
    public async Task Home_station_mutation_rejects_invalid_choices_and_maps_atomic_store_results()
    {
        await using var rig = await Rig.CreateAsync();
        var office = Rig.Stations.Get(60000610)!;
        var officeLocation = new HomeStationLocation(office.Id, office.TypeId, office.Name, office.SolarSystemId,
            office.ConstellationId, office.RegionId, office.OwnerId, false);
        var anotherOffice = new HomeStationLocation(60001000, 4023, "Remote Office", 30_002_775,
            2_000_407, 10_000_033, 1_000_006, false);
        var wormholeOffice = new HomeStationLocation(60001001, 4023, "Wormhole Office", 31_000_001,
            2_000_001, 11_000_001, 1_000_006, false);
        var resolver = Rig.CreateHomeStationResolver(rig.Characters,
            readCorporationOfficeCandidates: (_, _) => Task.FromResult<IReadOnlyList<HomeStationLocation>>(
                [officeLocation, anotherOffice, wormholeOffice]));
        var now = new DateTime(2026, 10, 11, 12, 34, 56, DateTimeKind.Utc).ToFileTimeUtc();

        var id = await rig.MakeAsync("Invalid Home Pilot");
        var (session, _) = Rig.Session(id);
        session.Set("stationid", new PyInt(60015249));
        var service = new HomeStationService(resolver, rig.Characters.TryChangeHomeStationAsync,
            rig.Characters.ResetRemoteHomeStationChangeTimeAsync, () => now);

        await AssertHomeStationValidationAsync(
            async () => { await Rig.CallAsync(service, session, "set_home_station", new PyInt(60009999), PyBool.True); }, 5);
        await AssertHomeStationValidationAsync(
            async () => { await Rig.CallAsync(service, session, "set_home_station", new PyInt(60015249), PyBool.True); }, 2);
        await AssertHomeStationValidationAsync(
            async () => { await Rig.CallAsync(service, session, "set_home_station", new PyInt(60001001), PyBool.True); }, 1);
        var remoteError = await Assert.ThrowsAsync<Eve.Services.MachoWrappedException>(
            async () => { await Rig.CallAsync(service, session, "set_home_station", new PyInt(60000610), PyBool.False); });
        Assert.Equal("homestation.error.RemoteChangeNotExpectedError", remoteError.Error.ClassName);
        Assert.Equal(60015249, (await rig.Characters.GetAsync(id, CancellationToken.None))!.HomeStationId);

        var racedId = await rig.MakeAsync("Raced Home Pilot");
        var (racedSession, _) = Rig.Session(racedId);
        racedSession.Set("stationid", new PyInt(60015249));
        var alreadyHomeRace = new HomeStationService(resolver, async (characterId, stationId, isRemote, fileTime, ct) =>
        {
            var first = await rig.Characters.TryChangeHomeStationAsync(characterId, stationId, isRemote, fileTime, ct);
            return first == HomeStationChangeResult.Changed
                ? await rig.Characters.TryChangeHomeStationAsync(characterId, stationId, isRemote, fileTime, ct)
                : first;
        }, rig.Characters.ResetRemoteHomeStationChangeTimeAsync, () => now);
        await AssertHomeStationValidationAsync(
            async () => { await Rig.CallAsync(alreadyHomeRace, racedSession, "set_home_station", new PyInt(60000610), PyBool.True); }, 2);
        Assert.Equal(60000610, (await rig.Characters.GetAsync(racedId, CancellationToken.None))!.HomeStationId);

        var cooldownId = await rig.MakeAsync("Cooldown Home Pilot");
        var (cooldownSession, _) = Rig.Session(cooldownId);
        cooldownSession.Set("stationid", new PyInt(60015249));
        var cooldownRace = new HomeStationService(resolver, async (characterId, stationId, isRemote, fileTime, ct) =>
        {
            var first = await rig.Characters.TryChangeHomeStationAsync(characterId, officeLocation.Id, true, fileTime, ct);
            return first == HomeStationChangeResult.Changed
                ? await rig.Characters.TryChangeHomeStationAsync(characterId, stationId, isRemote, fileTime, ct)
                : first;
        }, rig.Characters.ResetRemoteHomeStationChangeTimeAsync, () => now);
        await AssertHomeStationValidationAsync(
            async () => { await Rig.CallAsync(cooldownRace, cooldownSession, "set_home_station", new PyInt(60001000), PyBool.True); }, 3);
        Assert.Equal(officeLocation.Id, (await rig.Characters.GetAsync(cooldownId, CancellationToken.None))!.HomeStationId);
    }

    private sealed class RecordingPacketSink : Eve.Services.IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    private static async Task AssertHomeStationValidationAsync(Func<Task> action, int code)
    {
        var error = await Assert.ThrowsAsync<Eve.Services.MachoWrappedException>(action);
        Assert.Equal("homestation.validation.ChangeHomeStationValidationError", error.Error.ClassName);
        var header = Assert.IsType<PyTuple>(Assert.IsType<PyObjectEx>(error.Error.Payload).Header);
        var state = Assert.IsType<PyDict>(header.Items[2]);
        var errors = state.Entries.Single(entry => Assert.IsType<PyStr>(entry.Key).Value == "errors").Value;
        Assert.Equal(new PyList([new PyInt(code)]), errors);
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
