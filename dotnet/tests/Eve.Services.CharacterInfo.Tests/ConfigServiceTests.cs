using Eve.Characters;
using Eve.Corporations;
using Eve.Data;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.CharacterInfo;
using Eve.Sde;
using Eve.Universe;
using Npgsql;
using Xunit;

namespace Eve.Services.CharacterInfo.Tests;

public sealed class ConfigServicePgFactAttribute : FactAttribute
{
    public ConfigServicePgFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("EVE_PG_URL") is not { Length: > 0 })
            Skip = "EVE_PG_URL is not set";
    }
}

public sealed class ConfigServiceTests
{
    [Fact]
    public void Station_solar_systems_by_owner_returns_sorted_owner_keyvals()
    {
        const long ownerId = 1001;
        var stations = new StationCatalog([
            Station(3, ownerId, 30000144),
            Station(1, ownerId, 30000142),
            Station(2, ownerId, 30000142),
            Station(4, 1002, 30000146),
            Station(5, ownerId, 0),
        ]);
        var service = new ConfigService(null!, null!, stations, null!);
        var result = Assert.IsType<PyList>(service.GetStationSolarSystemsByOwner(new ServiceCall
        {
            Method = "GetStationSolarSystemsByOwner",
            Args = [new PyInt(ownerId)],
            Session = new ClientSession(),
        }));

        Assert.True(((Eve.Services.IService)service).HasMethod("GetStationSolarSystemsByOwner"));
        Assert.Equal(2, result.Items.Count);
        var first = Assert.IsType<PyObject>(result.Items[0]);
        Assert.Equal(new PyStr("util.KeyVal"), first.Name);
        var firstFields = Fields(first);
        Assert.Equal(3, firstFields.Count);
        Assert.Equal(new PyInt(1001), firstFields["ownerID"]);
        Assert.Equal(new PyInt(30000142), firstFields["solarSystemID"]);
        Assert.Equal(new PyInt(2), firstFields["stationCount"]);
        var secondFields = Fields(Assert.IsType<PyObject>(result.Items[1]));
        Assert.Equal(3, secondFields.Count);
        Assert.Equal(new PyInt(1001), secondFields["ownerID"]);
        Assert.Equal(new PyInt(30000144), secondFields["solarSystemID"]);
        Assert.Equal(new PyInt(1), secondFields["stationCount"]);
        Assert.Empty(Assert.IsType<PyList>(service.GetStationSolarSystemsByOwner(new ServiceCall
        {
            Method = "GetStationSolarSystemsByOwner",
            Args = [new PyInt(0)],
            Session = new ClientSession(),
        })).Items);
    }

    private static Station Station(long id, long ownerId, int systemId) =>
        new(id, 1531, $"Station {id}", ownerId, $"Owner {ownerId}", null, "", 0, 0,
            systemId, $"System {systemId}", 1, "Constellation", 1, "Region", 0.5, 0, 0);

    private static Dictionary<string, PyValue> Fields(PyObject value) =>
        Assert.IsType<PyDict>(value.Args).Entries.ToDictionary(
            entry => Assert.IsType<PyStr>(entry.Key).Value,
            entry => entry.Value,
            StringComparer.Ordinal);

    [ConfigServicePgFact]
    public async Task Client_cache_prime_rpc_shapes_cover_all_four_empires()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(url));
        CharacterNames.EnsureUnicodeSupport();
        var charactersSchema = $"cc_{Guid.NewGuid():N}"[..20];
        try
        {
            await PgCharacterStore.MigrateAsync(source, charactersSchema);
            var characters = new PgCharacterStore(source, charactersSchema);
            long characterId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                characterId = await characters.InsertAsync(connection, transaction,
                    new NewCharacter(1, "Config Cache Pilot", 1, 1, 1, 10, 35, 1373,
                        1000006, 500001, 60015249, 30100032, 20010001, 10001004, null, null));
                await transaction.CommitAsync();
            }

            var corporations = new CorporationStore(
                source, characters, null!, null!, null!, () => 0,
                Environment.GetEnvironmentVariable("EVE_CORPORATIONS_SCHEMA") ?? CorporationStore.DefaultSchema);
            var sde = new SdeReader(source);
            var stations = await StationCatalog.LoadAsync(sde);
            var solarSystems = await SolarSystemCatalog.LoadAsync(sde);
            var service = new ConfigService(characters, corporations, stations, solarSystems);
            var session = new ClientSession { SessionId = 1, ClientId = 2 };

            long[] corporationIds = [1000002, 1000006, 1000046, 1000063, 1000094];
            var ownerResult = await service.GetMultiOwnersEx(Call("GetMultiOwnersEx", [characterId, .. corporationIds], session));
            var owners = Rows(ownerResult,
                ["ownerID", "ownerName", "typeID", "gender", "ownerNameID"]);
            Assert.Equal(corporationIds.Length + 1, owners.Length);
            Assert.Equal(1373, Integer(owners.Single(row => Integer(row[0]) == characterId)[2]));
            foreach (var id in corporationIds)
            {
                var row = owners.Single(item => Integer(item[0]) == id);
                Assert.Equal(2, Integer(row[2]));
                Assert.False(string.IsNullOrWhiteSpace(Text(row[1])));
            }

            var tickerResult = await service.GetMultiCorpTickerNamesEx(Call("GetMultiCorpTickerNamesEx", corporationIds, session));
            var tickers = Rows(tickerResult,
                ["corporationID", "tickerName", "shape1", "shape2", "shape3", "color1", "color2", "color3"]);
            Assert.Equal(corporationIds.Length, tickers.Length);
            Assert.All(tickers, row => Assert.NotEqual("CORP", Text(row[1])));

            (long Station, int System)[] locations =
            [
                (60000004, 30002780), (60004480, 30003413),
                (60006100, 30002228), (60009034, 30002706),
            ];
            var requestedLocations = new long[] { 0L }.Concat(locations.SelectMany(location => new long[] { location.Station, location.System })).ToArray();
            var locationResult = await service.GetMultiLocationsEx(Call("GetMultiLocationsEx", requestedLocations, session));
            var locationRows = Rows(locationResult,
                ["locationID", "locationName", "solarSystemID", "x", "y", "z", "locationNameID"]);
            Assert.Equal(requestedLocations.Length, locationRows.Length);
            Assert.Equal("(none)", Text(locationRows.Single(row => Integer(row[0]) == 0)[6]));
            foreach (var (stationId, systemId) in locations)
            {
                var station = locationRows.Single(row => Integer(row[0]) == stationId);
                Assert.Equal(systemId, Integer(station[2]));
                Assert.DoesNotContain("Station ", Text(station[1]), StringComparison.Ordinal);
                var system = locationRows.Single(row => Integer(row[0]) == systemId);
                Assert.Equal(systemId, Integer(system[2]));
                Assert.DoesNotContain("System ", Text(system[1]), StringComparison.Ordinal);
            }
        }
        finally
        {
            await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS {charactersSchema} CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [ConfigServicePgFact]
    public async Task Alliance_short_names_return_a_client_rowset_and_ignore_deleted_or_unknown_ids()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(url));
        var schema = $"cfg_alliance_{Guid.NewGuid():N}"[..20];
        try
        {
            await CorporationStore.MigrateAsync(source, schema);
            await using (var seed = source.CreateCommand($"""
                INSERT INTO {schema}.owner_names(owner_id,name_key,ticker_key)
                VALUES (1400000001,'test corporation','test-corp'),
                       (1500000001,'test alliance','test-alliance'),
                       (1500000002,'deleted alliance','deleted-alliance');
                INSERT INTO {schema}.corporations(
                    id,name,ticker,ceo_id,creator_id,station_id,solar_system_id,tax_rate,lp_tax_rate,
                    description,url,member_limit,created_ms)
                VALUES (1400000001,'Test Corporation','TST',1,1,60000004,30000142,0,0,'','',10,0);
                INSERT INTO {schema}.alliances(
                    id,name,short_name,description,url,executor_corporation_id,creator_corporation_id,
                    creator_character_id,created_ms,founding_key,deleted)
                VALUES (1500000001,'Test Alliance','TAL','', '',1400000001,1400000001,1,0,'test-live',false),
                       (1500000002,'Deleted Alliance','DEL','', '',1400000001,1400000001,1,0,'test-deleted',true);
                """))
                await seed.ExecuteNonQueryAsync();

            var store = new CorporationStore(source, null!, null!, null!, null!, () => 0, schema);
            var service = new ConfigService(null!, store, null!, null!);
            Assert.True(((Eve.Services.IService)service).HasMethod("GetMultiAllianceShortNamesEx"));

            var value = await service.GetMultiAllianceShortNamesEx(
                Call("GetMultiAllianceShortNamesEx", [1500000001, 1500000002, 1500000003, 1500000001], new ClientSession()));
            var rows = Rows(value, ["allianceID", "shortName"]);
            var row = Assert.Single(rows);
            Assert.Equal(1500000001, Integer(row[0]));
            Assert.Equal("TAL", Text(row[1]));

            var empty = await service.GetMultiAllianceShortNamesEx(
                Call("GetMultiAllianceShortNamesEx", [1500000002, 1500000003], new ClientSession()));
            Assert.Empty(Assert.IsType<PyList>(empty).Items);
        }
        finally
        {
            await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private static ServiceCall Call(string method, IReadOnlyList<long> ids, ClientSession session) =>
        new()
        {
            Method = method,
            Args = [PyList.Of(ids.Select(id => (PyValue)new PyInt(id)).ToArray())],
            Session = session,
        };

    private static IReadOnlyList<PyValue>[] Rows(PyValue value, IReadOnlyList<string> expectedHeader)
    {
        var tupleSet = Assert.IsType<PyTuple>(value);
        var header = Assert.IsType<PyTuple>(tupleSet.Items[0]).Items.Select(Text).ToArray();
        Assert.Equal(expectedHeader, header);
        return Assert.IsType<PyTuple>(tupleSet.Items[1]).Items
            .Select(item => (IReadOnlyList<PyValue>)Assert.IsType<PyTuple>(item).Items)
            .ToArray();
    }

    private static long Integer(PyValue value) => Assert.IsType<PyInt>(value).Value;
    private static string Text(PyValue value) => Assert.IsType<PyStr>(value).Value;
}
