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
    /// <summary>The player bounties: the pools in the crimewatch schema, the placement debit in the economy.</summary>
    private Eve.Services.Crimewatch.PlayerBountyRpc? playerBounties;
    private Eve.Services.Crimewatch.PlayerBountyRpc PlayerBounties => playerBounties ??= new Eve.Services.Crimewatch.PlayerBountyRpc(
        new Eve.Crimewatch.PlayerBountyStore(source, crimewatchSchema), source, economyWallets, wallets, ResolveBountyOwnerAsync, now);

    /// <summary>What a bounty target is (EveJS <c>inferOwnerKind</c>): a known pilot, corporation or alliance, else the id range of its kind.</summary>
    private async ValueTask<Eve.Services.Crimewatch.PlayerBountyOwner> ResolveBountyOwnerAsync(long targetId, CancellationToken cancellationToken)
    {
        if (await characters.GetAsync(targetId, cancellationToken) is { } character)
            return new(Eve.Crimewatch.BountyOwnerKind.Character, character.CorporationId, character.AllianceId ?? 0);
        if (await corporations.GetAsync(targetId, cancellationToken) is { } corporation)
            return new(Eve.Crimewatch.BountyOwnerKind.Corporation, targetId, corporation.AllianceId ?? 0);
        if (await corporations.AllianceAsync(targetId, cancellationToken) is not null)
            return new(Eve.Crimewatch.BountyOwnerKind.Alliance, 0, targetId);
        var kind = targetId switch
        {
            (>= 90_000_000 and <= 97_999_999) or (>= 2_100_000_000 and <= 2_129_999_999) => Eve.Crimewatch.BountyOwnerKind.Character,
            >= 98_000_000 and <= 98_999_999 => Eve.Crimewatch.BountyOwnerKind.Corporation,
            >= 99_000_000 and <= 99_999_999 => Eve.Crimewatch.BountyOwnerKind.Alliance,
            _ => Eve.Crimewatch.BountyOwnerKind.Unknown,
        };
        return new(kind, kind == Eve.Crimewatch.BountyOwnerKind.Corporation ? targetId : 0, kind == Eve.Crimewatch.BountyOwnerKind.Alliance ? targetId : 0);
    }

    /// <summary>Crimewatch: the service, kill rights and the bounty proxy (kill rights and the player bounties).</summary>
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
                nowUnixMs: now,
                bountyRpcHandler: PlayerBounties.HandleAsync),
    ];
}
