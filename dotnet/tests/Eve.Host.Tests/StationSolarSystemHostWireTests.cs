using Eve.Characters;
using Eve.Marshal;
using Eve.Services.Core;
using Eve.Services.Station;
using Eve.Universe;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Station_GetSolarSystem_uses_the_requested_SDE_system_without_a_docked_station()
    {
        const int systemId = 30_000_142;
        const int constellationId = 20_000_001;
        const int regionId = 10_000_001;
        const int factionId = 500_001;
        await using var source = NpgsqlDataSource.Create("Host=localhost;Timeout=1");
        var service = new StationService(
            new StationCatalog([]),
            new PgCharacterStore(source),
            new OnlineCharacters(),
            () => FixedNowMs,
            solarSystems: new SolarSystemCatalog(
                [new SolarSystemInfo(systemId, "Jita", constellationId, regionId, 0.9, factionId)]));

        Start(configure: registry => registry.Register(service));
        var (front, open) = await OpenFrontAsync();

        var result = Assert.IsType<PyObject>(
            await HostRpcAsync(front, open, 1, "stationSvc", "GetSolarSystem", new PyInt(systemId)));
        Assert.Equal(new PyInt(systemId), WireField(result, "solarSystemID"));
        Assert.Equal(new PyInt(constellationId), WireField(result, "constellationID"));
        Assert.Equal(new PyInt(regionId), WireField(result, "regionID"));
        Assert.Equal(new PyInt(factionId), WireField(result, "factionID"));
        var longIdResult = Assert.IsType<PyObject>(
            await HostRpcAsync(front, open, 2, "stationSvc", "GetSolarSystem", new PyLong(systemId)));
        Assert.Equal(new PyInt(factionId), WireField(longIdResult, "factionID"));
        Assert.Same(PyNone.Instance,
            await HostRpcAsync(front, open, 3, "stationSvc", "GetSolarSystem", new PyLong(1)));
    }
}
