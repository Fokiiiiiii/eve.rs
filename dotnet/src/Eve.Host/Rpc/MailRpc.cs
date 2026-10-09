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
                now),
            new MailingListService(
                async ct =>
                {
                    var mailState = await ReadGameStoreTableAsync("mail", ct);
                    return mailState["mailingLists"] as JsonObject;
                },
                mutateMailingList),
    ];
}
