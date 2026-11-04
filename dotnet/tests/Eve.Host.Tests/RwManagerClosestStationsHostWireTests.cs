using Eve.Marshal;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Resource_war_station_lookup_returns_corporation_to_station_mapping_over_host()
    {
        const long systemId = 30_000_142;
        const long corporationId = 1_000_283;
        const long stationId = 60_000_123;
        var service = new RWManagerService(new RwManagerRpcCallbacks(
            ReadClosestStationsAsync: (requestedSystemId, _) =>
                ValueTask.FromResult<IReadOnlyDictionary<long, long>>(
                    requestedSystemId == systemId
                        ? new Dictionary<long, long> { [corporationId] = stationId }
                        : new Dictionary<long, long>())));
        Start(configure: registry => registry.Register(service));
        var (front, open) = await OpenFrontAsync();

        var stations = Assert.IsType<PyDict>(await HostRpcAsync(front, open, 1,
            "RWManager", "get_closest_rw_stations", new PyLong(systemId)));

        Assert.Equal(new PyInt(stationId), Assert.Single(stations.Entries).Value);
        Assert.Equal(new PyInt(corporationId), stations.Entries[0].Key);
    }
}
