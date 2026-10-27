using System.Net;
using System.Net.Sockets;
using Eve.Characters;
using Eve.Bookmarks;
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
    /// <summary>The character services in registry order. Each domain list is a contiguous run of the original list, so the order is unchanged.</summary>
    internal static T SingleRegisteredService<T>(IEnumerable<IService> registeredServices)
        where T : class, IService =>
        registeredServices
            .Select(service => service is IndustryInventoryChangePublisher publisher ? publisher.InnerService : service)
            .OfType<T>()
            .Single();

    private Task CharacterServiceList()
    {
        services = [
            .. CoreRpc(),
            .. CrimewatchRpc(),
            .. CharacterRpc(),
            .. StationRpc(),
            .. ItemRpc(),
            .. DungeonRpc(),
            .. InventoryRpc(),
            .. EconomyRpc(),
            .. CorporationRpc(),
            .. FittingRpc(),
            .. FleetRpc(),
            .. MailRpc(),
            .. RewardRpc(),
            .. IndustryRpc(),
            .. StructureRpc(),
            .. ProgressRpc(),
            .. AgentRpc(),
        ];
        services = [.. services, .. SpaceRpc()];
        services = [.. services, new RepackagingService(SingleRegisteredService<InvBrokerService>(services))];
        services = [.. services, new Eve.Services.Corporations.BillMgrService(
            corporations,
            corporationOfficeStore,
            economyWallets,
            stationId => stations.Get(stationId)?.OwnerId,
            now)];
        var sharedNotifier = new Eve.Services.Core.SharedFolderNotifier(bookmarks, MemberLists, characterSessions, AffiliationsOf);
        allianceRegistry.PlanSharedFolderAffiliationChangesAsync = sharedNotifier.PlanAffiliationChangesAsync;
        services = [.. services, allianceRegistry];
        UpkeepSharedFolders(sharedNotifier);
        services = [.. services, new Eve.Services.Core.OwnerGroupRefusals(new Eve.Services.Core.OwnerGroupManagerService(accessGroups, OwnerKind, characterSessions, sharedNotifier))];
        var bookmarkMgr = new Eve.Services.Core.AccessGroupBookmarkMgrService(bookmarks, StaticBookmarkTarget, MemberLists, characterSessions, sharedNotifier);
        var bookmarkService = new Eve.Services.Core.BookmarkRefusals(bookmarkMgr);
        if (spaceSessions is not null)
        {
            var beyonce = SingleRegisteredService<BeyonceService>(services);
            beyonce.ScanSites = scanBookmarkSites;
            beyonce.SpaceContainers = spaceContainers;
            beyonce.BookmarkTargetForCharacter = async (call, bookmarkId, cancellationToken) =>
            {
                var bookmark = await bookmarkMgr.ResolveActiveBookmarkAsync(call, bookmarkId);
                return bookmark is null ? null : new BookmarkWarpTarget(bookmark.LocationId, bookmark.ItemId, bookmark.X, bookmark.Y, bookmark.Z);
            };
            beyonce.ResolveBookmarkLocationTarget = async (call, itemId, cancellationToken) =>
            {
                var solarSystemId = call.Session.GetInt("solarsystemid") ?? 0;
                if (solarSystemId <= 0 || !spaceSessions.IsInSpace(call.Session)
                    || await spaceSessions.PositionOfAsync(call.Session, itemId, cancellationToken) is not { } target
                    || target.SolarSystemId != solarSystemId
                    || !double.IsFinite(target.X) || !double.IsFinite(target.Y) || !double.IsFinite(target.Z))
                    return null;
                return new Eve.Services.Space.ScanBookmarkTarget(solarSystemId, target.X, target.Y, target.Z);
            };
            beyonce.SaveBookmarkScanResult = (call, target, folderId, memo, note, expiryMode, subfolderId) =>
                Eve.Services.Core.BookmarkRefusals.WithRefusals(() => bookmarkMgr.CreateScanBookmarkAsync(call,
                    new BookmarkTarget(null, 5, target.SolarSystemId, target.X, target.Y, target.Z),
                    folderId, memo, note, expiryMode, subfolderId));
        }
        services = [.. services, bookmarkService];
        services = [.. services, new ActivityMgrService()];
        return Task.CompletedTask;
    }
}
