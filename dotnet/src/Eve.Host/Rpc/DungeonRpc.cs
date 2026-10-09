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
    /// <summary>Incursion anomalies and dungeons.</summary>
    private IReadOnlyList<IService> DungeonRpc() =>
    [
            new Eve.Services.Core.IncursionAnomalyService(
                "map",
                incursionAnomalyReadModel.ReadIncursionsAsync,
                incursionAnomalyReadModel.ReadAnomaliesAsync,
                new MapService(foundations.Reads, now) { ReadDataHook = ReadMapRpcDataAsync },
                now),
            new Eve.Services.Core.DungeonService(dungeonReadModel.ReadEditorCatalogAsync),
            new Eve.Services.Core.DungeonInstanceCacheMgrService(dungeonReadModel.ReadCacheSnapshotAsync, now),
            new Eve.Services.Core.IncursionAnomalyService(
                "taleMgr",
                incursionAnomalyReadModel.ReadIncursionsAsync,
                incursionAnomalyReadModel.ReadAnomaliesAsync,
                nowUnixMs: now),
            new Eve.Services.Core.IncursionAnomalyService(
                "scanMgr",
                incursionAnomalyReadModel.ReadIncursionsAsync,
                incursionAnomalyReadModel.ReadAnomaliesAsync,
                nowUnixMs: now,
                signalTrackerRegister: signalTrackerRegistrationProvider.RegisterAsync),
    ];
}
