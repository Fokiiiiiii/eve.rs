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
    /// <summary>The character services in registry order. Each domain list is a contiguous run of the original list, so the order is unchanged.</summary>
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
        services = [.. services, new RepackagingService(services.OfType<InvBrokerService>().Single())];
          services = [.. services, new Eve.Services.Corporations.BillMgrService(
              corporations,
              corporationOfficeStore,
              economyWallets,
              stationId => stations.Get(stationId)?.OwnerId,
              now)];
        services = [.. services, allianceRegistry];
        var sharedNotifier = new Eve.Services.Core.SharedFolderNotifier(bookmarks, MemberLists, characterSessions);
        ExpireSharedBookmarks(sharedNotifier);
        services = [.. services, new Eve.Services.Core.OwnerGroupRefusals(new Eve.Services.Core.OwnerGroupManagerService(accessGroups, OwnerKind, characterSessions, sharedNotifier))];
        services = [.. services, new Eve.Services.Core.BookmarkRefusals(new Eve.Services.Core.AccessGroupBookmarkMgrService(bookmarks, StaticBookmarkTarget, MemberLists, characterSessions, sharedNotifier))];
        return Task.CompletedTask;
    }
}
