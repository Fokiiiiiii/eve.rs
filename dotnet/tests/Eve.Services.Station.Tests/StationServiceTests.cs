using System.Text.Json.Nodes;
using Eve.Characters;
using Eve.Data;
using Eve.Macho;
using Eve.Marshal;
using Eve.Sde;
using Eve.Universe;
using Npgsql;

namespace Eve.Services.Station.Tests;

/// <summary>Skips a test, loudly, when there is no PostgreSQL to test against (see tools/dev/pg18.sh).</summary>
public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute()
    {
        if (Url is null)
        {
            Skip = "EVE_PG_URL is not set (see tools/dev/pg18.sh)";
        }
    }

    public static string? Url => Environment.GetEnvironmentVariable("EVE_PG_URL");
}

internal static class Data
{
    public const long ClockMs = 1_700_000_000_123;

    public static readonly Universe.Station Manifest = new(
        60015249, 92885, "Manifest V - AIR Laboratories Trade Center", 1000413, "AIR Laboratories", 500028, "Association for Interdisciplinary Research", 123, 40491241,
        30100032, "Manifest", 20010001, "Manifest District", 10001004, "Exordium", 1.0, 0.05, 4);

    public static readonly Universe.Station Paara = new(
        60000610, 4023, "Paara I - Deep Core Mining Inc. Mining Outpost", 1000006, "Deep Core Mining Inc.", 500001, "Caldari State", 7, 40003891,
        30002775, "Paara", 20000407, "Peyiri", 10000033, "The Citadel", 0.778535, 0.05, 4);

    public static readonly StationCatalog Stations = new([Manifest, Paara]);

    public static (ClientSession Session, Capture Sink) Session(long? station, long? character = null)
    {
        var sink = new Capture();
        var session = new ClientSession(sink) { SessionId = 1, ClientId = 2, UserId = 34 };
        if (station is { } id)
        {
            session.Set("stationid", new PyInt(id));
        }
        if (character is { } c)
        {
            session.CharacterId = c;
        }
        return (session, sink);
    }

    public static async Task<PyValue> CallAsync(IService service, ClientSession session, string method, params PyValue[] args) =>
        await service.CallAsync(new ServiceCall { Method = method, Args = args, Session = session });

    public static Dictionary<string, PyValue> Fields(PyValue keyVal)
    {
        var fields = new Dictionary<string, PyValue>();
        foreach (var entry in ((PyDict)((PyObject)keyVal).Args).Entries)
        {
            fields[JsSemantics.StringValue(entry.Key)!] = entry.Value;
        }
        return fields;
    }

    public static string[] Order(PyValue keyVal) => [.. ((PyDict)((PyObject)keyVal).Args).Entries.Select(e => JsSemantics.StringValue(e.Key)!)];
}

internal sealed class Capture : IPacketSink
{
    public List<PyValue> Sent { get; } = [];

    public void Send(PyValue packet) => Sent.Add(packet);
}

public class CachedResultTests
{
    [Fact]
    public void A_cached_result_carries_the_details_the_result_and_a_version_of_its_bytes()
    {
        var result = Py.KeyVal(("a", new PyInt(1)));
        var cached = (PyObject)ObjectCaching.MethodResult(result, Data.ClockMs);
        Assert.Equal("carbon.common.script.net.objectCaching.CachedMethodCallResult", JsSemantics.StringValue(cached.Name));

        var args = ((PyTuple)cached.Args).Items;
        Assert.Equal(3, args.Count);
        Assert.Equal("versionCheck", JsSemantics.StringValue(((PyDict)args[0]).Entries.Single().Key));
        Assert.Equal("run", JsSemantics.StringValue(((PyDict)args[0]).Entries.Single().Value));
        Assert.Equal(result, ((PySubStream)args[1]).Decoded);
        var version = (PyList)args[2];
        Assert.Equal(new PyLong(Py.FileTimeFromUnixMs(Data.ClockMs)), version.Items[0]);
        Assert.Equal(new PyInt(Checksums.SignedAdler32(MarshalCodec.Encode(result))), version.Items[1]);
    }

    [Fact]
    public void The_version_changes_when_the_result_does()
    {
        Assert.NotEqual(
            ((PyList)((PyTuple)((PyObject)ObjectCaching.MethodResult(Py.KeyVal(("a", new PyInt(1))), 1)).Args).Items[2]).Items[1],
            ((PyList)((PyTuple)((PyObject)ObjectCaching.MethodResult(Py.KeyVal(("a", new PyInt(2))), 1)).Args).Items[2]).Items[1]);
    }

    [Fact]
    public void Session_information_can_be_part_of_the_details()
    {
        var cached = (PyObject)ObjectCaching.MethodResult(new PyInt(1), 1, "1", "charid");
        var details = ((PyDict)((PyTuple)cached.Args).Items[0]).Entries.Select(e => (JsSemantics.StringValue(e.Key), JsSemantics.StringValue(e.Value))).ToArray();
        Assert.Equal([("versionCheck", "1"), ("sessionInfo", "charid")], details);
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("a", 0x00620062)]
    [InlineData("Wikipedia", 0x11E60398)] // the example of the Adler-32 definition
    public void The_checksum_is_adler_32_read_as_a_signed_number(string text, int expected) =>
        Assert.Equal(expected, Checksums.SignedAdler32(System.Text.Encoding.ASCII.GetBytes(text)));

    [Fact]
    public void A_large_checksum_is_negative()
    {
        // 0xFFFFFFFF as a signed number: bytes that drive both sums to their largest values are not needed, the sign is what counts
        Assert.True(Checksums.SignedAdler32(new byte[4000].Select(_ => (byte)255).ToArray()) < 0);
    }
}

public class StationRecordTests
{
    private static readonly JsonObject Recorded = (JsonObject)JsonNode.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "scenarios", "stations.json")))!;

    [PgFact]
    public async Task Every_recorded_station_record_is_reproduced_field_by_field_and_by_checksum()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(PgFactAttribute.Url!));
        var catalog = await StationCatalog.LoadAsync(new SdeReader(source));
        var service = new StationService(catalog, new PgCharacterStore(source, "characters"), new OnlineCharacters(), () => Data.ClockMs);
        var (session, _) = Data.Session(null);

        var count = 0;
        foreach (var node in (JsonArray)Recorded["stations"]!)
        {
            var id = (long)node!["id"]!;
            var answer = (PyObject)await Data.CallAsync(service, session, "GetStation", new PyInt(id));
            var args = ((PyTuple)answer.Args).Items;
            var record = ((PySubStream)args[1]).Decoded!;

            // field by field, in the recorded order, with the recorded types
            var recordedPairs = node["call"]!["ok"]!["args"]!["$tuple"]![1]!["$substream"]!["args"]!["$dict"]!.AsArray();
            var ours = ((PyDict)((PyObject)record).Args).Entries;
            Assert.Equal(recordedPairs.Count, ours.Count);
            for (var i = 0; i < ours.Count; i++)
            {
                Assert.Equal((string)recordedPairs[i]![0]!, JsSemantics.StringValue(ours[i].Key));
                var expected = recordedPairs[i]![1]!;
                var value = ours[i].Value;
                var name = (string)recordedPairs[i]![0]!;
                switch (value)
                {
                    case PyStr text: Assert.Equal((string)expected!, text.Value); break;
                    case PyInt integer: Assert.True((long)expected == integer.Value, $"{id} {name}: {expected} != {integer.Value}"); break;
                    case PyReal real: Assert.True(Math.Abs((double)expected - real.Value) < 1e-9, $"{id} {name}: {expected} != {real.Value}"); break;
                    default: Assert.Fail($"{id} {name}: unexpected {value.GetType().Name}"); break;
                }
                // a whole number is an integer, a fraction a real, on both sides
                if (value is PyInt or PyReal)
                {
                    Assert.Equal(expected.ToJsonString().Contains('.', StringComparison.Ordinal), value is PyReal);
                }
            }

            // the version's checksum is that of the marshalled record: the same bytes as the reference wrote
            var recordedChecksum = (int)node["call"]!["ok"]!["args"]!["$tuple"]![2]![1]!;
            Assert.Equal(recordedChecksum, ((PyInt)((PyList)args[2]).Items[1]).Value);
            count++;
        }
        Assert.InRange(count, 60, 70);
    }
}

public class StationServiceTests
{
    private static StationService Make(OnlineCharacters? online = null, PgCharacterStore? characters = null) =>
        new(Data.Stations, characters ?? new PgCharacterStore(NpgsqlDataSource.Create("Host=localhost"), "characters"), online ?? new OnlineCharacters(), () => Data.ClockMs);

    [Fact]
    public async Task An_unknown_station_or_a_bad_id_is_answered_with_none()
    {
        var (session, _) = Data.Session(null);
        foreach (var argument in new PyValue[] { new PyInt(1), new PyInt(0), new PyStr("x"), PyNone.Instance })
        {
            Assert.Equal(PyNone.Instance, await Data.CallAsync(Make(), session, "GetStation", argument));
        }
        Assert.Equal(PyNone.Instance, await Data.CallAsync(Make(), session, "GetStation"));
    }

    [Fact]
    public async Task The_record_of_a_known_station_is_cached_for_the_run_and_takes_a_long_id()
    {
        var (session, _) = Data.Session(null);
        var answer = (PyObject)await Data.CallAsync(Make(), session, "GetStation", new PyLong(60015249));
        Assert.Equal("carbon.common.script.net.objectCaching.CachedMethodCallResult", JsSemantics.StringValue(answer.Name));
        var record = ((PySubStream)((PyTuple)answer.Args).Items[1]).Decoded!;
        Assert.Equal(new PyStr("Manifest V - AIR Laboratories Trade Center"), Data.Fields(record)["stationName"]);
        Assert.Equal(new PyInt(StationService.AllServicesMask), Data.Fields(record)["serviceMask"]);
    }

    [Fact]
    public async Task The_item_bits_are_the_columns_of_the_row_the_client_builds_for_its_station()
    {
        var (session, _) = Data.Session(60015249);
        Assert.Equal(new PyTuple([new PyInt(1000413), new PyInt(60015249), new PyInt(123), new PyInt(92885)]), await Data.CallAsync(Make(), session, "GetStationItemBits"));
        var (elsewhere, _) = Data.Session(60000610);
        Assert.Equal(new PyTuple([new PyInt(1000006), new PyInt(60000610), new PyInt(7), new PyInt(4023)]), await Data.CallAsync(Make(), elsewhere, "GetStationItemBits"));
        var (nowhere, _) = Data.Session(null);
        Assert.Equal(PyNone.Instance, await Data.CallAsync(Make(), nowhere, "GetStationItemBits"));
    }

    [Fact]
    public async Task The_solar_system_answer_is_about_the_session_station_whatever_is_asked_for()
    {
        var (session, _) = Data.Session(60015249);
        var asked = await Data.CallAsync(Make(), session, "GetSolarSystem", new PyInt(30000142));
        Assert.Equal(await Data.CallAsync(Make(), session, "GetSolarSystem"), asked);
        Assert.Equal(
            ["solarSystemID", "solarSystemName", "constellationID", "constellationName", "regionID", "regionName", "security", "factionID", "factionName", "stationID",
             "stationName", "ownerID", "ownerName", "corporationID", "corporationName", "orbitID", "stationTypeID"],
            Data.Order(asked));
        var f = Data.Fields(asked);
        Assert.Equal((new PyInt(30100032), new PyStr("Manifest"), new PyStr("Manifest District"), new PyStr("Exordium")), (f["solarSystemID"], f["solarSystemName"], f["constellationName"], f["regionName"]));
        Assert.Equal(new PyInt(1), f["security"]); // a whole number is an integer
        Assert.Equal((new PyInt(500028), new PyStr("Association for Interdisciplinary Research"), new PyInt(40491241)), (f["factionID"], f["factionName"], f["orbitID"]));
        var (nowhere, _) = Data.Session(null);
        Assert.Equal(PyNone.Instance, await Data.CallAsync(Make(), nowhere, "GetSolarSystem"));
    }

    [Fact]
    public async Task No_alliance_holds_a_system_yet()
    {
        var (session, _) = Data.Session(60015249);
        Assert.Equal(new PyList([]), await Data.CallAsync(Make(), session, "GetAllianceSystems"));
        Assert.Equal(new PyList([]), await Data.CallAsync(Make(), session, "GetSystemsForAlliance", new PyInt(0)));
    }

    [Fact]
    public async Task A_session_that_is_not_docked_has_no_guests()
    {
        var (session, _) = Data.Session(null);
        Assert.Equal(new PyList([]), await Data.CallAsync(Make(), session, "GetGuests"));
    }

    [Fact]
    public void The_service_is_reached_by_both_of_its_names()
    {
        var service = Make();
        Assert.Equal("stationSvc", ((IService)service).Name);
        Assert.True(((IService)service).HasMethod("GetGuests"));
    }

    [PgFact]
    public async Task Map_station_info_is_a_cached_rowset()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(PgFactAttribute.Url!));
        var reads = new Eve.ReadSide.ReadSide(source);
        var service = new MapService(reads, () => Data.ClockMs);
        Assert.Equal("map", ((IService)service).Name);
        var (session, _) = Data.Session(null);
        var answer = await Data.CallAsync(service, session, "GetStationInfo");
        var cached = Assert.IsType<PyObject>(answer);
        Assert.Equal(ObjectCaching.ResultClass, JsSemantics.StringValue(cached.Name));

        var args = ((PyTuple)cached.Args).Items;
        var rowset = Assert.IsType<PyObject>(((PySubStream)args[1]).Decoded);
        var fields = Data.Fields(rowset);
        Assert.Equal(
            ["stationID", "solarSystemID", "operationID", "stationTypeID", "ownerID", "serviceMask", "constellationID", "regionID"],
            ((PyList)fields["header"]).Items.Select(JsSemantics.StringValue));
        Assert.Equal("util.Row", JsSemantics.StringValue(fields["RowClass"]));

        var lines = ((PyList)fields["lines"]).Items.Select(value => ((PyList)value).Items).ToArray();
        Assert.NotEmpty(lines);
        var stationIds = lines.Select(row => ((PyInt)row[0]).Value).ToArray();
        Assert.Equal(stationIds.Order().ToArray(), stationIds);
        Assert.All(lines, row =>
        {
            Assert.Equal(8, row.Count);
            Assert.Equal(StationService.AllServicesMask, ((PyInt)row[5]).Value);
        });

        var sde = new SdeReader(source);
        var staticStations = await sde.ReadAsync("npcStations");
        Assert.Equal(staticStations.Select(station => station.Key).Order().ToArray(), stationIds);

        var station = staticStations.Single(row => row.Key == stationIds[0]);
        var solarSystemId = station.Doc.GetProperty("solarSystemID").GetInt32();
        var system = (await sde.ReadAsync("mapSolarSystems", [(long)solarSystemId])).Single().Doc;
        var first = lines[0];
        Assert.Equal(station.Key, ((PyInt)first[0]).Value);
        Assert.Equal(solarSystemId, ((PyInt)first[1]).Value);
        Assert.Equal(station.Doc.GetProperty("operationID").GetInt32(), ((PyInt)first[2]).Value);
        Assert.Equal(station.Doc.GetProperty("typeID").GetInt32(), ((PyInt)first[3]).Value);
        Assert.Equal(station.Doc.GetProperty("ownerID").GetInt64(), ((PyInt)first[4]).Value);
        Assert.Equal(system.GetProperty("constellationID").GetInt32(), ((PyInt)first[6]).Value);
        Assert.Equal(system.GetProperty("regionID").GetInt32(), ((PyInt)first[7]).Value);
    }

    [PgFact]
    public async Task Map_security_modified_systems_and_station_counts_use_read_side()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(PgFactAttribute.Url!));
        var reads = new Eve.ReadSide.ReadSide(source);
        var service = new MapService(reads, () => Data.ClockMs);
        var (session, _) = Data.Session(null);

        Assert.Contains("map.securityModifiedSystems", reads.Names);
        Assert.Contains("map.stationCount", reads.Names);

        var modified = Assert.IsType<PyObject>(
            await Data.CallAsync(service, session, "GetSecurityModifiedSystems"));
        Assert.Equal("eve.common.script.sys.rowset.Rowset", JsSemantics.StringValue(modified.Name));
        var modifiedFields = Data.Fields(modified);
        Assert.Equal(
            ["solarSystemID"],
            ((PyList)modifiedFields["header"]).Items.Select(JsSemantics.StringValue));
        Assert.Equal("util.Row", JsSemantics.StringValue(modifiedFields["RowClass"]));
        Assert.Empty(((PyList)modifiedFields["lines"]).Items);

        var answer = Assert.IsType<PyList>(
            await Data.CallAsync(service, session, "GetStationCount"));
        var actual = answer.Items.Select(value =>
        {
            var tuple = Assert.IsType<PyTuple>(value).Items;
            Assert.Equal(2, tuple.Count);
            return (
                SolarSystemId: ((PyInt)tuple[0]).Value,
                StationCount: ((PyInt)tuple[1]).Value);
        }).ToArray();

        var sde = new SdeReader(source);
        var systems = await sde.ReadAsync("mapSolarSystems");
        var stations = await sde.ReadAsync("npcStations");
        var stationCounts = stations
            .GroupBy(station => (long)station.Doc.GetProperty("solarSystemID").GetInt32())
            .ToDictionary(group => group.Key, group => group.Count());
        var expected = systems
            .OrderBy(system => system.Key)
            .Select(system => (
                SolarSystemId: system.Key,
                StationCount: (long)stationCounts.GetValueOrDefault(system.Key, 0)))
            .ToArray();

        Assert.Equal(expected, actual);
        Assert.Contains(expected, row => row.StationCount == 0);
    }
}

public class GuestTests
{
    private static async Task<(PgCharacterStore Characters, NpgsqlDataSource Source, string Schema)> StoreAsync()
    {
        CharacterNames.EnsureUnicodeSupport();
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(PgFactAttribute.Url!));
        var schema = $"gst_{Guid.NewGuid():N}"[..20];
        await PgCharacterStore.MigrateAsync(source, schema);
        return (new PgCharacterStore(source, schema), source, schema);
    }

    private static async Task<long> MakeAsync(PgCharacterStore store, NpgsqlDataSource source, string name, long station, long corporation = 1000006)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await store.InsertAsync(connection, transaction, new NewCharacter(1, name, 1, 1, 1, 10, 35, 1373, corporation, 500001, station, 30100032, 20010001, 10001004, null, null));
        await transaction.CommitAsync();
        return id;
    }

    [PgFact]
    public async Task The_guests_are_the_characters_in_the_game_at_this_station()
    {
        var (store, source, schema) = await StoreAsync();
        try
        {
            var here1 = await MakeAsync(store, source, "Ann Lee", 60015249);
            var here2 = await MakeAsync(store, source, "Bob Roe", 60015249, corporation: 1000094);
            var away = await MakeAsync(store, source, "Cy Dee", 60000610);
            var offline = await MakeAsync(store, source, "Di Fay", 60015249);

            var online = new OnlineCharacters();
            online.TryEnter(here2, 2);
            online.TryEnter(here1, 1);
            online.TryEnter(away, 3);
            var service = new StationService(Data.Stations, store, online, () => Data.ClockMs);
            var (session, _) = Data.Session(60015249, here1);

            var guests = (PyList)await Data.CallAsync(service, session, "GetGuests");
            Assert.Equal(
                [new PyTuple([new PyInt(here1), new PyInt(1000006), new PyInt(0), new PyInt(0)]), new PyTuple([new PyInt(here2), new PyInt(1000094), new PyInt(0), new PyInt(0)])],
                guests.Items);
            Assert.DoesNotContain(guests.Items, g => ((PyTuple)g).Items[0] is PyInt { Value: var v } && (v == away || v == offline));

            online.Leave(2);
            Assert.Single(((PyList)await Data.CallAsync(service, session, "GetGuests")).Items);
        }
        finally
        {
            await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await command.ExecuteNonQueryAsync();
        }
    }
}
