using Eve.Host;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Host.Tests;

public sealed class HostMachoNetInfoTests
{
    [Fact]
    public void Cluster_game_statistics_count_active_pilots_by_system_and_docked_location()
    {
        var sessions = new CharacterSessions();
        var docked = new ClientSession();
        docked.Set("solarsystemid2", new PyInt(30_000_123));
        docked.Set("stationid", new PyInt(60_000_123));
        sessions.Enter(1, docked);

        var inSpace = new ClientSession();
        inSpace.Set("solarsystemid2", new PyInt(30_000_123));
        sessions.Enter(2, inSpace);

        var otherSystem = new ClientSession();
        otherSystem.Set("solarsystemid2", new PyInt(30_000_456));
        sessions.Enter(3, otherSystem);

        var info = new HostMachoNetInfo(new ServiceRegistry(), () => new PyDict([]), sessions);
        var counts = Assert.IsType<PyTuple>(info.ClusterGameStatisticsForClient());
        var systems = Assert.IsType<PyDict>(counts.Items[0]);
        var stations = Assert.IsType<PyDict>(counts.Items[1]);

        Assert.Contains(systems.Entries, entry => entry.Key.Equals(new PyInt(123)) && entry.Value.Equals(new PyInt(2)));
        Assert.Contains(systems.Entries, entry => entry.Key.Equals(new PyInt(456)) && entry.Value.Equals(new PyInt(1)));
        Assert.Contains(stations.Entries, entry => entry.Key.Equals(new PyInt(123)) && entry.Value.Equals(new PyInt(1)));
        Assert.Equal(new PyInt(1), counts.Items[2]);
    }
}
