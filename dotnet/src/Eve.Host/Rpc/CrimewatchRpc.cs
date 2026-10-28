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
        new Eve.Crimewatch.PlayerBountyStore(source, crimewatchSchema), source, economyWallets, wallets, ResolveBountyOwnerAsync, now, NotifyBountyPlacedAsync);

    /// <summary>

    /// corporation (113) and of an alliance's corporations (114) too. The placer is never told, and each recipient gets one notification per placement.
    /// </summary>
    private async ValueTask NotifyBountyPlacedAsync(Eve.Services.Crimewatch.BountyPlacement placement, CancellationToken cancellationToken)
    {
        var recipients = new List<long>();
        switch (placement.Kind)
        {
            case Eve.Crimewatch.BountyOwnerKind.Character:
                recipients.Add(placement.TargetId);
                break;
            case Eve.Crimewatch.BountyOwnerKind.Corporation:
                recipients.AddRange((await characters.ForCorporationAsync(placement.TargetId, cancellationToken)).Select(member => member.Id));
                break;
            case Eve.Crimewatch.BountyOwnerKind.Alliance:
                foreach (var member in await corporations.AllianceMembersAsync(placement.TargetId, cancellationToken))
                    recipients.AddRange((await characters.ForCorporationAsync(member.CorporationId, cancellationToken)).Select(pilot => pilot.Id));
                break;
            default:
                return;
        }
        var typeId = placement.Kind switch
        {
            Eve.Crimewatch.BountyOwnerKind.Character => BountyPlacedCharTypeId,
            Eve.Crimewatch.BountyOwnerKind.Corporation => BountyPlacedCorpTypeId,
            _ => BountyPlacedAllianceTypeId,
        };
        var data = new System.Text.Json.Nodes.JsonObject { ["bountyPlacerID"] = placement.PlacerId, ["bounty"] = (double)placement.Amount };
        foreach (var recipient in recipients.Distinct().Where(id => id > 0 && id != placement.PlacerId).OrderBy(id => id))
        {
            await mailSendWriter.WriteBountyPlacedNotificationAsync(recipient, placement.PlacerId, typeId, data,
                $"bounty-placed:{placement.Key}:receiver:{recipient}", cancellationToken);
        }
    }


    private const int BountyPlacedCharTypeId = 112, BountyPlacedCorpTypeId = 113, BountyPlacedAllianceTypeId = 114;


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
    private async Task ApplyTagsForSecStatusAsync(ServiceCall call, double targetStatus, double reportedCurrentStatus)
    {
        if (call.Session.CharacterId is not { } characterId || characterId <= 0)
            throw new InvalidOperationException("Security tag exchange requires an authenticated character.");
        if (call.Session.GetInt("stationid") is not { } stationId ||
            stations.Get(stationId) is not { } station ||
            station.OwnerId is not (1000125 or 1000137) ||
            station.Security <= 0 || station.Security >= 0.45)
            throw new InvalidOperationException("Security tag exchange is only available in eligible low-security stations.");

        var requirements = Eve.Services.Crimewatch.SecurityTagRequirements.Calculate(reportedCurrentStatus, targetStatus);
        var typeIds = requirements.Keys.Order().ToArray();
        await dogma.EnsureTypesAsync(typeIds, call.Cancellation);
        var feeAttribute = dogma.AttributeId("securityProcessingFee")
            ?? throw new InvalidOperationException("The SDE has no securityProcessingFee attribute.");
        var fees = 0m;
        foreach (var (typeId, quantity) in requirements)
        {
            var type = dogma.Type(typeId)
                ?? throw new InvalidOperationException($"The SDE has no security tag type {typeId}.");
            var feePerTag = type.Attributes.GetValueOrDefault(feeAttribute);
            if (!double.IsFinite(feePerTag) || feePerTag < 0)
                throw new InvalidOperationException($"The SDE has an invalid security tag fee for type {typeId}.");
            fees = checked(fees + checked((decimal)feePerTag * quantity));
        }
        if (decimal.Round(fees, 2) != fees)
            throw new InvalidOperationException("The SDE security tag fees are not whole ISK cents.");

        var timestamp = now();
        var operationKey = $"security-tag-hand-in:{call.Session.SessionId}:{call.CallId}";
        var itemChanges = new List<Eve.Ships.ItemChanged>();
        decimal balance = 0;
        double previousStatus;
        double nextStatus;
        await using (var connection = await source.OpenConnectionAsync(call.Cancellation))
        await using (var transaction = await connection.BeginTransactionAsync(call.Cancellation))
        {
            await economyWallets.LockAsync(connection, transaction, [characterId], call.Cancellation);
            var relation = await foundations.Relations.GetAsync(
                Eve.Relations.RelationKinds.SecurityStatus.Name,
                characterId,
                Eve.Relations.RelationStore.World,
                connection,
                transaction,
                call.Cancellation,
                forUpdate: true);
            previousStatus = relation?.Value ?? 0d;
            if (reportedCurrentStatus != previousStatus)
                throw new InvalidOperationException("Security status changed before the tag exchange.");

            requirements = Eve.Services.Crimewatch.SecurityTagRequirements.Calculate(previousStatus, targetStatus);
            var tags = await items.LockInLocationAsync(
                connection, transaction, stationId, characterId, Eve.Inventory.ItemFlags.Hangar, typeIds, call.Cancellation);
            var counts = tags.GroupBy(item => item.TypeId).ToDictionary(group => group.Key, group => group.Sum(item => item.Quantity));
            foreach (var (typeId, quantity) in requirements)
                if (counts.GetValueOrDefault(typeId) < quantity)
                    throw new InvalidOperationException($"Not enough security tags of type {typeId}.");

            var toConsume = requirements.ToDictionary(entry => entry.Key, entry => entry.Value);
            foreach (var item in tags)
            {
                var remaining = toConsume.GetValueOrDefault(item.TypeId);
                if (remaining <= 0) continue;
                var consumed = Math.Min(item.Quantity, remaining);
                var afterQuantity = item.Quantity - consumed;
                if (afterQuantity == 0)
                    await items.DeleteAsync(connection, transaction, item.Id, call.Cancellation);
                else
                    await items.SetQuantityAsync(connection, transaction, item.Id, afterQuantity, call.Cancellation);
                itemChanges.Add(new Eve.Ships.ItemChanged(
                    item with { Quantity = afterQuantity }, item.LocationId, item.Flag, item.Quantity));
                toConsume[item.TypeId] = remaining - consumed;
            }

            nextStatus = previousStatus + requirements.Values.Sum() * 0.5;
            var attrs = new Dictionary<string, object?>
            {
                ["eventTypeID"] = Eve.Crimewatch.SecurityStatusEventTypeIds.HandInTags,
                ["locationID"] = stationId,
                ["modification"] = nextStatus - previousStatus,
            };
            await foundations.Relations.SetAsync(
                connection, transaction,
                Eve.Relations.RelationKinds.SecurityStatus.Name,
                characterId, Eve.Relations.RelationStore.World, nextStatus,
                "hand_in_tags", attrs, cancellationToken: call.Cancellation);
            if (fees > 0)
                balance = await economyWallets.AdjustAsync(
                    connection, transaction, characterId, -fees, 1000125, 117, "security_tag_hand_in",
                    timestamp, operationKey, call.Cancellation);
            await transaction.CommitAsync(call.Cancellation);
        }

        Eve.Services.ItemRow Row(Eve.Inventory.Item item)
        {
            var type = dogma.Type(item.TypeId)
                ?? throw new InvalidOperationException($"The SDE has no security tag type {item.TypeId}.");
            return new Eve.Services.ItemRow(
                item.Id, item.TypeId, item.OwnerId, item.LocationId, item.Flag,
                item.Singleton ? -1 : item.Quantity, type.GroupId, type.CategoryId);
        }

        foreach (var change in itemChanges)
            Eve.Services.Ships.ShipNotifications.SendItemChange(call.Session, change, Row);
        if (fees > 0)
            economyWallets.AnnounceCommitted(new WalletBalanceChange(characterId, 1000, balance));
        Eve.Services.Crimewatch.SecurityStatusPenalties.Tell(call.Session, previousStatus, nextStatus, timestamp);
    }

    private IReadOnlyList<IService> CrimewatchRpc() =>
    [
            // security status is the character's relation of that kind (Eve.Relations); crimewatch only asks for it
            new Eve.Services.Crimewatch.CrimewatchService(
                crimewatchStore, crimewatchRuntime, now,
                securityStatusOf, ApplyTagsForSecStatusAsync),
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
                bountyRpcHandler: PlayerBounties.HandleAsync,
                resolveOrganization: async (characterId, token) =>
                {
                    var member = await corporations.MemberAsync(characterId, token);
                    if (member is null)
                        return (0, 0);
                    var corporation = await corporations.GetAsync(member.CorporationId, token);
                    return (member.CorporationId, corporation?.AllianceId ?? 0);
                }),
    ];
}
