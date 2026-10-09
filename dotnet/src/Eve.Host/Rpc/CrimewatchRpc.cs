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
    /// <summary>Crimewatch: the service, kill rights and the bounty proxy kill right.</summary>
    private IReadOnlyList<IService> CrimewatchRpc() =>
    [
            // security status is the character's relation of that kind (Eve.Relations); crimewatch only asks for it
            new Eve.Services.Crimewatch.CrimewatchService(
                crimewatchStore, crimewatchRuntime, now,
                securityStatusOf),
            new Eve.Services.Crimewatch.KillRightMgrService(
                crimewatchStore, crimewatchRuntime, economyWallets, wallets, now,
                message => Console.Error.WriteLine($"eve-host: crimewatch: {message}"))
            {
                KillRightUsedNotification = (activation, ct) => mailSendWriter.WriteKillRightNotificationAsync(
                    activation.OwnerCharacterId, activation.TargetCharacterId,
                    Eve.Services.Crimewatch.KillRightNotificationTypes.Used,
                    new JsonObject { ["charID"] = activation.TargetCharacterId },
                    $"kill-right-used:{activation.ActivationId}", ct)
            },
            new Eve.Services.Crimewatch.BountyProxyKillRightService(
                store: crimewatchStore,
                notificationWriter: mailSendWriter.WriteKillRightNotificationAsync,
                nowUnixMs: now),
    ];
}
