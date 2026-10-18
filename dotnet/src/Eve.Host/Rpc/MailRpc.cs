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
    /// <summary>Insurance, mail, notifications and mailing lists.</summary>
    private IReadOnlyList<IService> MailRpc() =>
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
                mailSendWriter.SendAsync),
            new NotificationMgrService(
                async (characterId, ct) =>
                {
                    var notifications = await ReadGameStoreTableAsync("notifications", ct);
                    return (notifications["boxes"] as JsonObject)?[characterId.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonObject;
                },
                now,
                mutate: async (characterId, operation, _, notificationIds, requestingSession, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    var boxPath = $"/boxes/{characterId}";
                    if (mailCache.Read("notifications", boxPath).Data is not JsonObject box
                        || box["byID"] is not JsonObject records)
                        return;

                    var requested = notificationIds.ToHashSet();
                    var changed = new List<long>();
                    foreach (var (key, node) in records.ToArray())
                    {
                        if (node is not JsonObject record
                            || !long.TryParse(key, System.Globalization.NumberStyles.None,
                                System.Globalization.CultureInfo.InvariantCulture, out var id)
                            || id <= 0)
                            continue;
                        var selected = operation switch
                        {
                            "MarkAllAsProcessed" => true,
                            "MarkAsProcessed" or "DeleteNotifications" => requested.Contains(id),
                            _ => false,
                        };
                        if (!selected)
                            continue;
                        if (operation is "MarkAllAsProcessed" or "MarkAsProcessed")
                        {
                            if (record["processed"]?.GetValueKind() == JsonValueKind.True)
                                continue;
                            record["processed"] = true;
                        }
                        else
                        {
                            records.Remove(key);
                        }
                        changed.Add(id);
                    }
                    if (changed.Count == 0)
                        return;
                    if (operation == "DeleteNotifications" && box["order"] is JsonArray order)
                    {
                        var removed = changed.ToHashSet();
                        for (var index = order.Count - 1; index >= 0; index--)
                        {
                            if (long.TryParse(order[index]?.ToString(),
                                System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture,
                                out var id) && removed.Contains(id))
                                order.RemoveAt(index);
                        }
                    }
                    var write = mailCache.Write("notifications", boxPath, box);
                    if (!write.Success)
                        throw new InvalidOperationException($"Could not mutate notifications for {characterId}: {write.ErrorMsg}");
                    await mailCache.FlushTableAsync("notifications", ct);
                    if (operation == "DeleteNotifications"
                        && characterSessions.Of(characterId) is { } otherSession
                        && !ReferenceEquals(otherSession, requestingSession))
                    {
                        otherSession.SendNotification("OnNotificationDeleted", "clientID",
                            new PyTuple([new PyList([.. changed.Select(id => (PyValue)new PyInt(id))])]));
                    }
                }),
            new MailingListService(
                async ct =>
                {
                    var mailState = await ReadGameStoreTableAsync("mail", ct);
                    return mailState["mailingLists"] as JsonObject;
                },
                mutateMailingList),
    ];
}
