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
    [ConfigServicePgFact]
    public async Task Client_cache_prime_rpc_shapes_cover_all_four_empires()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(url));
        var characters = new PgCharacterStore(source, Environment.GetEnvironmentVariable("EVE_CHARACTERS_SCHEMA") ?? PgCharacterStore.DefaultSchema);
        var corporations = new CorporationStore(
            source, characters, null!, null!, null!, () => 0,
            Environment.GetEnvironmentVariable("EVE_CORPORATIONS_SCHEMA") ?? CorporationStore.DefaultSchema);
        var sde = new SdeReader(source);
        var stations = await StationCatalog.LoadAsync(sde);
        var solarSystems = await SolarSystemCatalog.LoadAsync(sde);
        var service = new ConfigService(characters, corporations, stations, solarSystems);
        var session = new ClientSession { SessionId = 1, ClientId = 2 };

        long characterId = 140000052;
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
