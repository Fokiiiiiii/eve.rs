using Eve.Characters;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Station;
using Eve.Universe;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Station_reads_are_dispatched_through_host_with_client_transport_kwargs()
    {
        const long stationId = 60_000_123;
        const long ownerId = 98_000_001;
        const long allianceId = 99_000_001;
        const int systemId = 30_000_142;
        await using var source = NpgsqlDataSource.Create("Host=localhost;Timeout=1");
        var station = new Station(stationId, 1531, "Test Station", ownerId, "Test Owner", null, "", 7, 40_000_001,
            systemId, "Test System", 20_000_001, "Test Constellation", 10_000_001, "Test Region", 0.5, 0.05, 4,
            11, 12, 13);
        var service = new StationService(
            new StationCatalog([station]),
            new PgCharacterStore(source),
            new OnlineCharacters(),
            () => FixedNowMs,
            (id, _) => ValueTask.FromResult<IReadOnlyList<long>>(id == allianceId ? [systemId] : []));

        Start(
            configure: registry =>
            {
                registry.Register(service);
                registry.RegisterAlias("station", "stationSvc");
            },
            sessionOpened: session =>
            {
                session.Set("stationid", new PyLong(stationId));
                session.Set("allianceid", new PyLong(allianceId));
            });
        var (front, open) = await OpenFrontAsync();

        Assert.Equal(new PyList([]), await HostRpcAsync(front, open, 1, "station", "GetGuests"));

        var allianceSystems = Assert.IsType<PyList>(
            await HostRpcAsync(front, open, 2, "stationSvc", "GetAllianceSystems"));
        Assert.Equal(new PyList([new PyInt(systemId)]), allianceSystems);
        Assert.Equal(allianceSystems,
            await HostRpcAsync(front, open, 3, "stationSvc", "GetSystemsForAlliance", new PyLong(allianceId)));

        var stationBits = Assert.IsType<PyTuple>(
            await HostRpcAsync(front, open, 4, "stationSvc", "GetStationItemBits"));
        Assert.Equal([new PyInt(ownerId), new PyInt(stationId), new PyInt(7), new PyInt(1531)], stationBits.Items);

        var ownedStations = Assert.IsType<PyList>(
            await HostRpcAsync(front, open, 5, "stationSvc", "GetStationsForOwner", new PyLong(ownerId)));
        var ownedStation = Assert.Single(ownedStations.Items);
        Assert.Equal(new PyInt(stationId), WireField(ownedStation, "stationID"));
        Assert.Equal("Test Station", WireText(WireField(ownedStation, "stationName")));

        var stationResult = Assert.IsType<PyObject>(
            await HostRpcAsync(front, open, 6, "stationSvc", "GetStation", new PyLong(stationId)));
        Assert.Equal("carbon.common.script.net.objectCaching.CachedMethodCallResult", WireText(stationResult.Name));
        var stationRecord = Assert.IsType<PySubStream>(
            Assert.IsType<PyTuple>(stationResult.Args).Items[1]).Decoded!;
        Assert.Equal("Test Station", WireText(WireField(stationRecord, "stationName")));
        Assert.Equal(new PyInt(systemId), WireField(stationRecord, "solarSystemID"));

        Assert.Same(PyNone.Instance,
            await HostRpcAsync(front, open, 7, "stationSvc", "GetStation", new PyLong(1)));
    }

    [Fact]
    public async Task Structure_docking_routes_reach_the_registered_service_callbacks()
    {
        var transitions = new List<string>();
        var service = new StructureRpcService("structureDocking", new StructureRpcCallbacks
        {
            DockAsync = (_, structureId, shipId) =>
            {
                transitions.Add($"dock:{structureId}:{shipId}");
                return ValueTask.CompletedTask;
            },
            UndockAsync = (_, structureId, shipId, ignoreContraband) =>
            {
                transitions.Add($"undock:{structureId}:{shipId}:{ignoreContraband}");
                return ValueTask.CompletedTask;
            },
        });
        Start(configure: registry => registry.Register(service));
        var (front, open) = await OpenFrontAsync();

        Assert.Same(PyNone.Instance,
            await HostRpcAsync(front, open, 1, "structureDocking", "Dock", new PyLong(1_000_000_000_051), new PyLong(1_000_000_000_052)));
        Assert.Same(PyNone.Instance,
            await HostRpcAsync(front, open, 2, "structureDocking", "Undock", new PyLong(1_000_000_000_051), new PyLong(1_000_000_000_052)));

        Assert.Equal(["dock:1000000000051:1000000000052", "undock:1000000000051:1000000000052:False"], transitions);
    }
}
