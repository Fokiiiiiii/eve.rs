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
    /// <summary>Account and market proxy.</summary>
    private IReadOnlyList<IService> EconomyRpc() =>
    [
            new AccountService(wallets, corporations, economyWallets)
            {
                ReadContactCost = ReadContactCostAsync,
                WriteContactCost = WriteContactCostAsync,
            },
            new Eve.Services.Market.MarketProxyService(market, marketTrading, marketTopology, characters, corporations, economyWallets, marketCatalog, now, marketExpiry, characterSessions),
    ];

    private ValueTask<int?> ReadContactCostAsync(long characterId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var read = mailCache.Read("characters", $"/{characterId}/characterSettings/cspaCharge");
        if (!read.Success)
        {
            if (read.ErrorMsg == "ENTRY_NOT_FOUND")
                return ValueTask.FromResult<int?>(0);
            throw new InvalidOperationException($"Could not read character CSPA charge: {read.ErrorMsg}.");
        }
        if (read.Data is null)
            return ValueTask.FromResult<int?>(null);
        if (read.Data is not JsonValue json || !json.TryGetValue<int>(out var cost) || cost is < 0 or > 1_000_000)
            throw new InvalidOperationException("The persisted character CSPA charge is invalid.");
        return ValueTask.FromResult<int?>(cost);
    }

    private ValueTask WriteContactCostAsync(long characterId, int? cost, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var write = mailCache.Write("characters", $"/{characterId}/characterSettings/cspaCharge",
            cost is { } amount ? JsonValue.Create(amount) : null);
        if (!write.Success)
            throw new InvalidOperationException($"Could not persist character CSPA charge: {write.ErrorMsg}.");
        return ValueTask.CompletedTask;
    }
}
