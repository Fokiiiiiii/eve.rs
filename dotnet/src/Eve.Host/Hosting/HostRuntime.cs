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

/// <summary>What the host runs after composition: the server and the registry, and the disposables shut down in the original order.</summary>
internal sealed class HostRuntime(HostServer server, ServiceRegistry registry, LegacyGameStore? legacy, Foundations? foundations,
    SpaceSessions? space, Eve.Services.Crimewatch.CrimewatchRuntime? crimewatch, SimClient? simulation)
{
    internal HostServer Server { get; } = server;

    internal ServiceRegistry Registry { get; } = registry;

    /// <summary>The durable scheduler of the host (the expiry jobs of the market are kept in it); null when no foundations were built.</summary>
    internal Eve.Scheduler.DurableScheduler? Scheduler { get; } = foundations?.Scheduler;

    internal async Task ShutdownAsync()
    {
        if (legacy is not null) await legacy.DisposeAsync();
        if (foundations is not null)
        {
            await foundations.DisposeAsync();
        }
        space?.Dispose();
        crimewatch?.Dispose();
        if (simulation is not null)
        {
            await simulation.DisposeAsync();
        }
    }
}
