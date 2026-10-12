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
    /// <summary>Stations, trade, development indices and eject.</summary>
    private IReadOnlyList<IService> StationRpc() =>
    [
            new StationService(stations, characters, online, now, sovereigntyReadModel.ReadAllianceSystemsAsync, officeManager: officeManager, seedObserverGuestLedger: stationGuestLedger.SeedObserverGuestLedger, characterSessions: characterSessions),
            new Eve.Services.Station.TradeMgrService(characterSessions, tradeAuthority, itemTypes, stations, now),
            new Eve.Services.Station.ReprocessingService(source, items, itemTypes, stations, new Eve.Services.Station.SdeReprocessingData(sde, adjustedPrices), foundations.Relations, skills, economyWallets, wallets, now),
            new Eve.Services.Station.RepairService(source, items, itemTypes, stations, dogma, modules, adjustedPrices, economyWallets, wallets, now),
            new Eve.Host.DevIndexManagerRpcService(sovereigntyReadModel.ReadAllDevelopmentIndicesAsync),
            new Eve.Host.EjectRpcService(),
    ];
}
