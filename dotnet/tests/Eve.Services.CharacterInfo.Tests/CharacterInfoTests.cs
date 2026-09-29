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
        new Station(60015249, 92885, "Manifest V - AIR Laboratories Trade Center", 1000413, 123, 30100032, 20010001, 10001004, 0.9),
        new Station(60000610, 4023, "Paara I - Deep Core Mining Inc. Mining Outpost", 1000006, 7, 30002775, 20000407, 10000033, 0.7785),
    ]);

    private readonly string _characters = $"ci_{Guid.NewGuid():N}"[..20];
    private readonly string _wallet = $"cw_{Guid.NewGuid():N}"[..20];

    private Rig(NpgsqlDataSource source)
    {
        Source = source;
        Characters = new PgCharacterStore(source, _characters);
        Wallets = new PgWalletStore(source, _wallet);
        CharMgr = new CharMgrService(Characters, Stations);
        Account = new AccountService(Wallets);
    }

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
    public async Task The_description_and_the_creation_date_are_the_characters()
    {
        await using var rig = await Rig.CreateAsync();
        var id = await rig.MakeAsync("Ann Lee");
        var (session, _) = Rig.Session(id);
        Assert.Equal(new PyStr(""), await Rig.CallAsync(rig.CharMgr, session, "GetCharacterDescription", Id(id)));
        var created = ((PyLong)await Rig.CallAsync(rig.CharMgr, session, "GetCharacterCreationDate", Id(id))).Value;
        var now = Py.FileTimeFromUnixMs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.InRange(created, now - (5 * 60 * 10_000_000L), now + (60 * 10_000_000L));
        Assert.Equal(PyNone.Instance, await Rig.CallAsync(rig.CharMgr, session, "GetCharacterDescription", Id(1)));
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
        Assert.Equal(new PyList([]), await Ask("GetRecentShipKillsAndLosses", Id(10), Id(0)));

        var settings = (PyTuple)await Ask("GetSettingsInfo");
        Assert.Equal(Id(0), settings.Items[1]);
        Assert.Equal(
            "630000000000000000010000004300000073040000006900005328010000004e280000000028000000002800000000280000000073080000003c737472696e673e740100000066010000007300000000",
            Convert.ToHexStringLower(((PyBuffer)settings.Items[0]).Value.Span));

        var contacts = Fields(await Ask("GetContactList"));
        Assert.Equal(new[] { "addresses", "blocked" }, contacts.Keys);
        var addresses = (PyDict)((PyObject)contacts["addresses"]).Args;
        Assert.Equal("eve.common.script.sys.rowset.Rowset", JsSemantics.StringValue(((PyObject)contacts["addresses"]).Name));
        Assert.Equal(new PyList(new[] { "contactID", "inWatchlist", "relationshipID", "labelMask" }.Select(c => (PyValue)new PyStr(c)).ToList()), addresses.Entries[0].Value);
        Assert.Equal(new PyToken("util.Row"), addresses.Entries[2].Value);
        Assert.Equal(new PyList([]), addresses.Entries[3].Value);
        Assert.Equal(new PyList([new PyStr("senderID")]), ((PyDict)((PyObject)contacts["blocked"]).Args).Entries[0].Value);
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
