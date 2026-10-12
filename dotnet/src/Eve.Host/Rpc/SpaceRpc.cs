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

internal sealed partial class CharacterServicesComposition
{
    /// <summary>The hacking minigame's open attempts (the analyzer opens them, the hackingMgr service plays them).</summary>
    private readonly Eve.Services.Space.HackingAttempts hackingAttempts = new();

    async ValueTask<Eve.Services.Space.SpaceRpcItem?> ReadTravelRpcItemAsync(long itemId, CancellationToken cancellationToken)
    {
        var item = await items.GetAsync(itemId, cancellationToken);
        if (item is null) return null;
        var type = await itemTypes.GetAsync(item.TypeId, cancellationToken);
        return new Eve.Services.Space.SpaceRpcItem(item.Id, item.OwnerId, item.TypeId, type?.GroupId ?? 0, item.Quantity);
    }

    /// <summary>Space services that run after the others: filaments, jumps, warp vectors, wormholes and structure guests.</summary>
    private IReadOnlyList<IService> SpaceRpc()
    {
        // without a space simulation (EVE_SIM=off) there is no reach and no analyzer: the service is still registered
        var hackingSite = new Eve.Services.Space.SpaceHackingSite(spaceSessions);
        var hackingMgr = new Eve.Services.Space.HackingMgrService(hackingAttempts, hackingSite);
        if (spaceSessions is not null)
            spaceSessions.HackingService = hackingMgr;
        // the containers of the sites are the site content's (the space's SiteContainerHacks, set by the space composition)
        hackingSite.Find = (characterId, containerId, cancellationToken) => spaceSessions?.HackableContainers is { } hacks
            ? hacks.FindAsync(containerId, cancellationToken)
            : ValueTask.FromResult<HackableTarget?>(null);
        hackingSite.Ended = (session, target, won, cancellationToken) => spaceSessions?.HackableContainers is { } hacks
            ? hacks.EndedAsync(target, won, session.CharacterId ?? 0, cancellationToken)
            : ValueTask.CompletedTask;
        return
    [
            hackingMgr,
            new Eve.Services.Space.PvpFilamentMgrService(),
            new Eve.Services.Space.RandomJumpMgrService(ReadTravelRpcItemAsync),
            new Eve.Services.Space.WarpVectorMgrService(ReadTravelRpcItemAsync),
            new Eve.Services.Space.WormholeMgrService(
                spaceSessions,
                readPolarization: (endId, characterId, _) => ValueTask.FromResult(spaceSessions?.PolarizationOf(endId, characterId)),
                jump: async (call, endId, cancellationToken) => spaceSessions is null
                    ? null
                    : await spaceSessions.WormholeJumpAsync(call.Session, endId, call.BoundObjectId, cancellationToken)),
            new Eve.Services.Core.StructureGuestsService(
            (structureId, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var guests = characterSessions.InStructure(structureId)
                    .Where(session => session.CharacterId is > 0)
                    .Select(session => new Eve.Services.Core.StructureGuestSnapshot(
                        session.CharacterId!.Value,
                        Math.Max(0, session.GetInt("corpid") ?? 0),
                        Math.Max(0, session.GetInt("allianceid") ?? 0),
                        Math.Max(0, session.GetInt("warfactionid") ?? 0)))
                    .ToArray();
                return ValueTask.FromResult<IReadOnlyList<Eve.Services.Core.StructureGuestSnapshot>>(guests);
            }, seedObserverGuestLedger: stationGuestLedger.SeedObserverGuestLedger),
    ];
    }
}
