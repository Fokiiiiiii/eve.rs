using System.Net;
using System.Net.Sockets;
using Eve.Characters;
using Eve.Data;
using Eve.Host;
using Eve.Identity;
using Eve.Dogma;
using Eve.Fitting;
using Eve.GameStore;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.Inventory;
using Eve.Ships;
using Eve.Marshal;
using Eve.Npc;
using Eve.Sde;
using Eve.Skills;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;
using Sentry;
using Eve.Services;
using Eve.Services.CharacterSelect;
using Eve.Services.Core;
using Eve.Services.Dogma;
using Eve.Services.Inventory;
using Eve.Services.CharacterInfo;
using Eve.Services.Skills;
using Eve.Services.Slash;
using Eve.Services.Space;
using Eve.Space;
using Eve.Services.Station;
using Eve.Services.Wallet;
using static Eve.Host.HostConfiguration;

namespace Eve.Host;

/// <summary>machoNet's view of this host: the registered services, with the locations the client routes by.</summary>
public sealed class HostMachoNetInfo(ServiceRegistry registry, Func<PyDict> config, CharacterSessions? characterSessions = null) : IMachoNetInfo
{
    public IReadOnlyList<KeyValuePair<string, string?>> Services() =>
        [.. registry.ServiceNames.Select(name => new KeyValuePair<string, string?>(name, ServiceLocations.Of(name)))];

    public PyDict GlobalConfig() => config();

    public int UserCount() => 0;

    public PyValue ClusterGameStatisticsForClient()
    {
        var systems = new Dictionary<long, long>();
        var stations = new Dictionary<long, long>();
        foreach (var session in characterSessions?.All() ?? [])
        {
            var systemId = session.GetInt("solarsystemid2") ?? session.GetInt("solarsystemid");
            if (systemId is not > 30_000_000)
                continue;

            var mapSystemId = systemId.Value - 30_000_000;
            systems[mapSystemId] = systems.GetValueOrDefault(mapSystemId) + 1;
            if (session.DockedLocationId is not null)
                stations[mapSystemId] = stations.GetValueOrDefault(mapSystemId) + 1;
        }

        static PyDict ToPyDict(Dictionary<long, long> counts) => new([.. counts.Select(entry =>
            new KeyValuePair<PyValue, PyValue>(new PyInt(entry.Key), new PyInt(entry.Value)))]);

        return Py.Tuple(ToPyDict(systems), ToPyDict(stations), new PyInt(1));
    }
}
