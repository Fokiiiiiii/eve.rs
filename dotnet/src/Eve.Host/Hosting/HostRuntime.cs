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
    SpaceSessions? space, Eve.Services.Crimewatch.CrimewatchRuntime? crimewatch, SimClient? simulation, CharacterSessions? characterSessions = null,
    PublicGateway.PublicGatewayRoutes? publicGateway = null, NpgsqlDataSource? dataSource = null)
{
    internal HostServer Server { get; } = server;

    /// <summary>The sessions that have a character in the game (null when no characters are served: no EVE_PG_URL).</summary>
    internal CharacterSessions? CharacterSessions { get; } = characterSessions;

    internal ServiceRegistry Registry { get; } = registry;

    /// <summary>The public protobuf gateway: the answers by request type, the notice streams and the authority of requests (no answers without PostgreSQL: no characters, no plans).</summary>
    internal PublicGateway.PublicGatewayRoutes PublicGateway { get; } = publicGateway ?? new PublicGateway.PublicGatewayRoutes(
        new Dictionary<string, PublicGateway.GatewayHandler>(), new PublicGateway.NoticeHub(),
        new PublicGateway.GatewayAuthority((_, _) => Task.FromResult<long?>(null), () => DateTimeOffset.UtcNow));

    /// <summary>The durable scheduler of the host (the expiry jobs of the market are kept in it); null when no foundations were built.</summary>
    internal Eve.Scheduler.DurableScheduler? Scheduler { get; } = foundations?.Scheduler;

    /// <summary>The event bus of the host (its subscribers are the host's own); null when no foundations were built.</summary>
    internal Eve.Events.EventBus? Events { get; } = foundations?.Events;

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
        if (dataSource is not null) await dataSource.DisposeAsync();
    }
}
