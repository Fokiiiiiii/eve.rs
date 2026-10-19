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
    async Task<PersistenceMailSendRecipients?> ResolveMailSendRecipientsAsync(
        Eve.Services.Core.MailSendRequest request,
        Eve.Services.ClientSession session,
        CancellationToken cancellationToken)
    {
        if (request.SenderCharacterId <= 0 || session.CharacterId != request.SenderCharacterId)
            return null;
        var sender = await characters.GetAsync(request.SenderCharacterId, cancellationToken);
        if (sender is null)
            return null;

        var recipientIds = new List<long>();
        var recipientLabelMasks = new Dictionary<long, int>();
        void AddRecipient(long characterId, int labelMask)
        {
            if (characterId <= 0)
                return;
            if (recipientLabelMasks.TryGetValue(characterId, out var prior))
                recipientLabelMasks[characterId] = prior | labelMask;
            else
            {
                recipientIds.Add(characterId);
                recipientLabelMasks.Add(characterId, labelMask);
            }
        }

        var senderLabelMask = 2;
        foreach (var characterId in request.CharacterRecipientIds.Distinct())
        {
            if (characterId <= 0 || await characters.GetAsync(characterId, cancellationToken) is null)
                return null;
            AddRecipient(characterId, 1);
            if (characterId == request.SenderCharacterId)
                senderLabelMask |= 1;
        }

        if (request.MailingListId is { } mailingListId)
        {
            var listKey = mailingListId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var list = mailCache.Read("mail", $"/mailingLists/{listKey}").Data as JsonObject;
            if (list is null || (list["deleted"] is JsonValue deleted && deleted.TryGetValue<bool>(out var isDeleted) && isDeleted))
                return null;
            var members = list["members"] as JsonObject;
            var ownerId = long.TryParse(list["ownerID"]?.ToString(),
                System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsedOwner)
                ? parsedOwner : 0;
            var senderRole = 0d;
            if (members?[request.SenderCharacterId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is { } storedRole)
                double.TryParse(storedRole.ToString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out senderRole);
            if (ownerId != request.SenderCharacterId && Math.Truncate(senderRole) <= 0)
                return null;
            if (members is not null)
            {
                foreach (var (key, value) in members)
                {
                    if (!long.TryParse(key, System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out var characterId) || characterId <= 0 ||
                        !double.TryParse(value?.ToString(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var role) || Math.Truncate(role) == 0)
                        continue;
                    if (await characters.GetAsync(characterId, cancellationToken) is not null)
                        AddRecipient(characterId, 1);
                }
            }
            if (ownerId > 0 && await characters.GetAsync(ownerId, cancellationToken) is not null)
                AddRecipient(ownerId, 1);
        }

        if (request.CorporationOrAllianceRecipientIds is { Count: > 0 } organizationIds)
        {
            foreach (var organizationId in organizationIds.Distinct())
            {
                if (organizationId <= 0)
                    return null;
                if (await corporations.AllianceAsync(organizationId, cancellationToken) is not null)
                {
                    senderLabelMask |= 8;
                    foreach (var memberCorporation in await corporations.AllianceMembersAsync(organizationId, cancellationToken))
                        foreach (var member in await corporations.MembersAsync(memberCorporation.CorporationId, cancellationToken))
                            AddRecipient(member.CharacterId, 1 | 8);
                }
                else if (await corporations.GetAsync(organizationId, cancellationToken) is not null)
                {
                    senderLabelMask |= 4;
                    foreach (var member in await corporations.MembersAsync(organizationId, cancellationToken))
                        AddRecipient(member.CharacterId, 1 | 4);
                }
                else
                    return null;
            }
        }

        return recipientIds.Count == 0
            ? null
            : new PersistenceMailSendRecipients(recipientIds, sender.Name, senderLabelMask, recipientLabelMasks);
    }

    int ReadUnreadMailCount(long characterId)
    {
        var mailState = mailCache.Read("mail", null).Data as JsonObject;
        var messages = mailState?["messages"] as JsonObject;
        var statuses = (mailState?["mailboxes"] as JsonObject)?[
            characterId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is JsonObject mailbox
            ? mailbox["statuses"] as JsonObject
            : null;
        if (messages is null || statuses is null)
            return 0;

        var unread = 0;
        const long readMask = 1;
        const long trashedMask = 8;
        foreach (var (messageId, statusNode) in statuses)
        {
            if (!messages.ContainsKey(messageId) || statusNode is not JsonObject status)
                continue;
            var statusMask = long.TryParse(status["statusMask"]?.ToString(),
                System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture,
                out var parsedMask) ? parsedMask : 0;
            if ((statusMask & readMask) == 0 && (statusMask & trashedMask) == 0)
                unread++;
        }
        return unread;
    }

    async Task SendNewCharacterWelcomeMailAsync(long characterId, CancellationToken cancellationToken)
    {
        try
        {
            var character = await characters.GetAsync(characterId, cancellationToken);
            var corporation = character is null
                ? null
                : await corporations.GetAsync(character.CorporationId, cancellationToken);
            if (character is null || corporation is null ||
                await mailSendWriter.SendWelcomeMailAsync(
                    corporation.Id, corporation.Name, character.Id, character.Name, cancellationToken) is not > 0)
                throw new InvalidOperationException("The new character or starter corporation is unavailable.");
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[CharacterCreation] Welcome mail failed for character {characterId}: {error.Message}");
        }
    }

    private async Task MailSend()
    {
        await Eve.Calendar.CalendarStore.MigrateAsync(source, CancellationToken.None);
        calendarStore = new Eve.Calendar.CalendarStore(source);
        calendarAudience = new PersistenceCalendarAudienceReader(
            async (corporationId, ct) =>
                (await corporations.MembersAsync(corporationId, ct)).Select(member => member.CharacterId).ToArray(),
            async (allianceId, ct) =>
                (await corporations.AllianceMembersAsync(allianceId, ct)).Select(member => member.CorporationId).ToArray());

        mailSendWriter = new PersistenceMailSendHostAdapter(
            table => mailCache.Read(table, null).Data as JsonObject ?? new JsonObject(),
            (table, path, data) => mailCache.Write(table, path, data),
            (table, cancellationToken) => mailCache.FlushTableAsync(table, cancellationToken),
            (table, cancellationToken) => mailStore.LoadTableAsync(table, createIfMissing: false, cancellationToken),
            ResolveMailSendRecipientsAsync,
            now,
            (characterId, args) => characterSessions.Of(characterId)?.SendNotification("OnNotificationReceived", "clientID", args));
        SubscribeMoonOwnerNotifications();
        if (allianceMaintenanceBillWorker is { } maintenanceWorker)
        {
            maintenanceWorker.BillNotificationWriter = mailSendWriter.WriteAllianceMaintenanceBillNotificationAsync;
            await maintenanceWorker.ReconcileAsync();
        }
    }


    private Eve.Host.Persistence.Mail.PersistenceTransactionMailbox? notificationMailbox;

    private void SubscribeMoonOwnerNotifications()
    {
        var mailbox = new Eve.Host.Persistence.Mail.PersistenceTransactionMailbox(
            source, mailStore, mailCache, mailStoreOwner, mailSendWriter.MutationGate, now,
            (characterId, args) => characterSessions.Of(characterId)?.SendNotification("OnNotificationReceived", "clientID", args));
        notificationMailbox = mailbox;
        new Eve.Host.Persistence.Structures.MoonOwnerNotifications(
            source, structureStore, moonExtractionStore, characters, mailbox).Subscribe(foundations.Events);
        new Eve.Host.Persistence.Structures.ReinforcementChangedNotifications(characters, mailbox)
            .Subscribe(foundations.Events);
        new Eve.Host.Persistence.Sovereignty.EntosisOwnerNotifications(
            async (allianceId, ct) => (await corporations.AllianceMembersAsync(allianceId, ct)).Select(member => member.CorporationId).ToArray(),
            characters, mailbox).Subscribe(foundations.Events);
        sovClaimNotifications = new Eve.Host.Persistence.Sovereignty.SovereigntyClaimNotifications(
            async (allianceId, ct) => (await corporations.AllianceMembersAsync(allianceId, ct)).Select(member => member.CorporationId).ToArray(),
            characters, mailbox);
        new Eve.Host.Persistence.Structures.StructureFuelOwnerNotifications(structureStore, characters, characterSessions, dogma,
            Eve.Host.Persistence.Structures.StructureFuelOwnerNotifications.ToMailbox(mailbox))
            .Subscribe(foundations.Events);
    }

}
