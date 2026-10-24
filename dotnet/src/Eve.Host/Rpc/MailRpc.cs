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

internal sealed class NotificationMgrHostAdapter(
    Eve.GameStore.GameStoreCache cache,
    Func<string, CancellationToken, Task<System.Text.Json.Nodes.JsonObject>> readTable,
    Eve.Services.CharacterSessions characterSessions)
{
    public async ValueTask<System.Text.Json.Nodes.JsonObject?> ReadAsync(
        long characterId,
        CancellationToken cancellationToken)
    {
        var notifications = await readTable("notifications", cancellationToken);
        return (notifications["boxes"] as System.Text.Json.Nodes.JsonObject)?[
            characterId.ToString(System.Globalization.CultureInfo.InvariantCulture)] as System.Text.Json.Nodes.JsonObject;
    }

    public async ValueTask MutateAsync(
        long characterId,
        string operation,
        long? groupId,
        IReadOnlyList<long> notificationIds,
        Eve.Services.ClientSession? requestingSession,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var boxPath = $"/boxes/{characterId}";
        if (cache.Read("notifications", boxPath).Data is not System.Text.Json.Nodes.JsonObject box
            || box["byID"] is not System.Text.Json.Nodes.JsonObject records)
            return;

        var requested = notificationIds.ToHashSet();
        var changed = new List<long>();
        foreach (var (key, node) in records.ToArray())
        {
            if (node is not System.Text.Json.Nodes.JsonObject record
                || !long.TryParse(key, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var id)
                || id <= 0)
                continue;
            var selected = operation switch
            {
                "MarkAllAsProcessed" or "DeleteAllNotifications" => true,
                "MarkAsProcessed" or "DeleteNotifications" => requested.Contains(id),
                "MarkGroupAsProcessed" or "DeleteGroupNotifications" =>
                    groupId is > 0
                    && long.TryParse(record["groupID"]?.ToString(), System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var storedGroupId)
                    && storedGroupId == groupId.Value,
                _ => false,
            };
            if (!selected)
                continue;
            if (operation is "MarkAllAsProcessed" or "MarkAsProcessed" or "MarkGroupAsProcessed")
            {
                if (record["processed"]?.GetValueKind() == System.Text.Json.JsonValueKind.True)
                    continue;
                record["processed"] = true;
            }
            else if (operation is "DeleteNotifications" or "DeleteGroupNotifications" or "DeleteAllNotifications")
            {
                records.Remove(key);
            }
            else
            {
                continue;
            }
            changed.Add(id);
        }
        if (changed.Count == 0)
            return;

        if (operation is "DeleteNotifications" or "DeleteGroupNotifications" or "DeleteAllNotifications"
            && box["order"] is System.Text.Json.Nodes.JsonArray order)
        {
            var removed = changed.ToHashSet();
            for (var index = order.Count - 1; index >= 0; index--)
            {
                if (long.TryParse(order[index]?.ToString(),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var id) && removed.Contains(id))
                    order.RemoveAt(index);
            }
        }

        var write = cache.Write("notifications", boxPath, box);
        if (!write.Success)
            throw new InvalidOperationException($"Could not mutate notifications for {characterId}: {write.ErrorMsg}");
        await cache.FlushTableAsync("notifications", cancellationToken);
        if (operation is "DeleteNotifications" or "DeleteGroupNotifications" or "DeleteAllNotifications"
            && characterSessions.Of(characterId) is { } otherSession
            && !ReferenceEquals(otherSession, requestingSession))
        {
            otherSession.SendNotification("OnNotificationDeleted", "clientID",
                new Eve.Marshal.PyTuple([
                    new Eve.Marshal.PyList([.. changed.Select(id => (Eve.Marshal.PyValue)new Eve.Marshal.PyInt(id))])
                ]));
        }
    }
}

internal sealed partial class CharacterServicesComposition
{
    /// <summary>Insurance, mail, notifications and mailing lists.</summary>
    private IReadOnlyList<IService> MailRpc()
{
    var notificationMgr = new NotificationMgrHostAdapter(mailCache, ReadGameStoreTableAsync, characterSessions);
    return
    [
        insuranceService,
        new MailService(
            ct => ReadGameStoreTableAsync("mail", ct),
            async (characterId, messageId, requestingSession, ct) =>
            {
                var statusPath = $"/mailboxes/{characterId}/statuses/{messageId}";
                if (mailCache.Read("mail", statusPath).Data is not JsonObject status)
                    return;
                var priorMask = long.TryParse(status["statusMask"]?.ToString(),
                    System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture,
                    out var parsedMask) ? parsedMask : 0;
                if ((priorMask & 1) != 0)
                    return;
                status["statusMask"] = priorMask | 1L;
                var write = mailCache.Write("mail", statusPath, status);
                if (!write.Success)
                    throw new InvalidOperationException($"Could not mark mail {messageId} read: {write.ErrorMsg}");
                await mailCache.FlushTableAsync("mail", ct);
                if (characterSessions.Of(characterId) is { } otherSession &&
                    !ReferenceEquals(otherSession, requestingSession))
                {
                    otherSession.SendNotification("OnMailUpdatedByExternal", "clientID",
                        new PyTuple([new PyList([new PyInt(messageId)]), PyBool.True, PyNone.Instance]));
                }
            },
            mailSendWriter.SendAsync,
            mailSendWriter.SendGmChatPokeAsync),
        new NotificationMgrService(notificationMgr.ReadAsync, now, mutate: notificationMgr.MutateAsync),
        new MailingListService(
            async ct =>
            {
                var mailState = await ReadGameStoreTableAsync("mail", ct);
                return mailState["mailingLists"] as JsonObject;
            },
            mutateMailingList),
    ];
}
}
