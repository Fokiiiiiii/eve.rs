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

public sealed class PersistenceMailSendHostAdapter(
    Func<string, JsonObject> readCacheTable,
    Func<string, string?, JsonNode?, Eve.GameStore.WriteResult> write,
    Func<string, CancellationToken, Task<int>> flushTableAsync,
    Func<string, CancellationToken, Task<JsonObject>> readPersistedTableAsync,
    Func<Eve.Services.Core.MailSendRequest, Eve.Services.ClientSession, CancellationToken, Task<PersistenceMailSendRecipients?>> resolveRecipientsAsync,
    Func<long> nowUnixMs,
    Action<long, Eve.Marshal.PyValue[]> sendNotification)
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public async Task<long?> SendAsync(
        Eve.Services.Core.MailSendRequest request,
        Eve.Services.ClientSession session,
        CancellationToken cancellationToken)
    {
        if (request.SenderCharacterId <= 0 || session.CharacterId != request.SenderCharacterId)
            return null;

        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            var recipients = await resolveRecipientsAsync(request, session, cancellationToken);
            if (recipients is null)
                return null;

            var mailState = readCacheTable("mail");
            var messages = mailState["messages"] as JsonObject ?? new JsonObject();
            var mailboxes = mailState["mailboxes"] as JsonObject ?? new JsonObject();
            var messageId = NextMailId(messages, mailboxes);
            var sentFiletime = DateTimeOffset.FromUnixTimeMilliseconds(nowUnixMs()).UtcDateTime.ToFileTimeUtc();
            var messageKey = Key(messageId);
            var directRecipients = request.CharacterRecipientIds.Distinct().ToArray();
            var corpOrAllianceIds = request.CorporationOrAllianceRecipientIds?.Distinct().ToArray();
            var message = new JsonObject
            {
                ["messageID"] = messageId,
                ["senderID"] = request.SenderCharacterId,
                ["senderName"] = recipients.SenderName,
                ["toCharacterIDs"] = LongArray(directRecipients),
                ["toListID"] = OptionalLong(request.MailingListId),
                ["toCorpOrAllianceID"] = corpOrAllianceIds is null ? null : LongArray(corpOrAllianceIds),
                ["title"] = request.Title,
                ["subject"] = request.Title,
                ["body"] = request.Body,
                ["sentDate"] = sentFiletime,
                ["replyToMessageID"] = OptionalLong(request.ReplyToMessageId),
                ["forwardedFromMessageID"] = OptionalLong(request.ForwardedFromMessageId),
                ["statusMask"] = 0,
                ["labelMask"] = 0,
                ["read"] = false,
                ["trashed"] = false,
                ["replied"] = false,
                ["forwarded"] = false,
            };

            var statuses = new Dictionary<(long CharacterId, long MessageId), JsonObject>();
            foreach (var recipientId in recipients.CharacterIds.Distinct().Where(id => id != request.SenderCharacterId))
            {
                statuses[(recipientId, messageId)] = new JsonObject
                {
                    ["statusMask"] = 0,
                    ["labelMask"] = recipients.LabelMasksByCharacterId.GetValueOrDefault(recipientId, 1),
                };
            }

            var senderDirectlyIncluded = directRecipients.Contains(request.SenderCharacterId);
            var senderRead = !senderDirectlyIncluded &&
                request.MailingListId is null &&
                request.CorporationOrAllianceRecipientIds is null;
            statuses[(request.SenderCharacterId, messageId)] = new JsonObject
            {
                ["statusMask"] = senderRead ? 1 : 0,
                ["labelMask"] = recipients.SenderLabelMask,
            };

            var sourceFlags = new Dictionary<long, long>();
            if (request.ReplyToMessageId is { } replyId && replyId > 0)
                sourceFlags[replyId] = sourceFlags.GetValueOrDefault(replyId) | 2;
            if (request.ForwardedFromMessageId is { } forwardedId && forwardedId > 0)
                sourceFlags[forwardedId] = sourceFlags.GetValueOrDefault(forwardedId) | 4;
            foreach (var (sourceId, flag) in sourceFlags)
            {
                if (messages[Key(sourceId)] is not JsonObject source ||
                    Number(source["senderID"]) != request.SenderCharacterId ||
                    (mailboxes[Key(request.SenderCharacterId)] as JsonObject)?["statuses"] is not JsonObject senderStatuses ||
                    senderStatuses[Key(sourceId)] is not JsonObject sourceStatus)
                    continue;
                var updated = (JsonObject)sourceStatus.DeepClone();
                updated["statusMask"] = Number(sourceStatus["statusMask"]) | flag;
                statuses[(request.SenderCharacterId, sourceId)] = updated;
            }

            RequireWrite("mail", $"/messages/{messageKey}", message);
            foreach (var ((characterId, statusMessageId), status) in statuses)
                RequireWrite("mail", $"/mailboxes/{Key(characterId)}/statuses/{Key(statusMessageId)}", status);

            await FlushUntilVisibleAsync("mail", persisted =>
            {
                var persistedMessages = persisted["messages"] as JsonObject;
                if (persistedMessages?[messageKey] is not JsonObject savedMessage ||
                    Number(savedMessage["senderID"]) != request.SenderCharacterId ||
                    !string.Equals(Text(savedMessage["title"]), request.Title, StringComparison.Ordinal) ||
                    !string.Equals(Text(savedMessage["body"]), request.Body, StringComparison.Ordinal))
                    return false;

                var persistedMailboxes = persisted["mailboxes"] as JsonObject;
                foreach (var ((characterId, statusMessageId), expected) in statuses)
                {
                    var actual = (persistedMailboxes?[Key(characterId)] as JsonObject)?["statuses"] is JsonObject rows
                        ? rows[Key(statusMessageId)] as JsonObject
                        : null;
                    if (actual is null ||
                        Number(actual["statusMask"]) != Number(expected["statusMask"]) ||
                        Number(actual["labelMask"]) != Number(expected["labelMask"]))
                        return false;
                }
                return true;
            }, cancellationToken);

            var notifications = readCacheTable("notifications");
            var notificationBoxes = notifications["boxes"] as JsonObject ?? new JsonObject();
            var nextNotificationId = NextNotificationId(notificationBoxes);
            var notificationIds = new Dictionary<long, long>();
            foreach (var recipientId in recipients.CharacterIds.Distinct().Where(id => id != request.SenderCharacterId))
            {
                var notificationId = nextNotificationId++;
                notificationIds[recipientId] = notificationId;
                var notificationMessage = (JsonObject)message.DeepClone();
                var notification = new JsonObject
                {
                    ["notificationID"] = notificationId,
                    ["typeID"] = 1004,
                    ["senderID"] = request.SenderCharacterId,
                    ["receiverID"] = recipientId,
                    ["processed"] = false,
                    ["created"] = sentFiletime,
                    ["data"] = new JsonObject
                    {
                        ["senderName"] = recipients.SenderName,
                        ["subject"] = request.Title,
                        ["msg"] = notificationMessage,
                    },
                };
                RequireWrite("notifications", $"/boxes/{Key(recipientId)}/byID/{Key(notificationId)}", notification);
            }

            if (notificationIds.Count > 0)
            {
                await FlushUntilVisibleAsync("notifications", persisted =>
                {
                    var boxes = persisted["boxes"] as JsonObject;
                    return notificationIds.All(pair =>
                        (boxes?[Key(pair.Key)] as JsonObject)?["byID"] is JsonObject rows &&
                        rows[Key(pair.Value)] is JsonObject saved &&
                        Number(saved["typeID"]) == 1004 &&
                        Number(saved["receiverID"]) == pair.Key);
                }, cancellationToken);

                foreach (var (recipientId, notificationId) in notificationIds)
                {
                    var data = NotificationData(
                        request.SenderCharacterId, recipients.SenderName,
                        request.Title, message, sentFiletime);
                    sendNotification(recipientId,
                    [
                        new Eve.Marshal.PyLong(notificationId),
                        new Eve.Marshal.PyInt(1004),
                        new Eve.Marshal.PyLong(request.SenderCharacterId),
                        Py.FileTimeLong(sentFiletime),
                        data,
                    ]);
                }
            }

            return messageId;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task<long?> SendWelcomeMailAsync(
        long senderId,
        string senderName,
        long recipientCharacterId,
        string characterName,
        CancellationToken cancellationToken)
    {
        if (senderId <= 0 || recipientCharacterId <= 0 ||
            string.IsNullOrWhiteSpace(senderName) || string.IsNullOrWhiteSpace(characterName))
            return null;

        const string title = "Welcome to New Eden";
        var body = $"Welcome to New Eden, {System.Net.WebUtility.HtmlEncode(characterName)}.<br><br>" +
            "Your capsuleer career begins now. Fly safe.";

        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            var mailState = readCacheTable("mail");
            var messages = mailState["messages"] as JsonObject ?? new JsonObject();
            var mailboxes = mailState["mailboxes"] as JsonObject ?? new JsonObject();
            var mailboxStatuses = (mailboxes[Key(recipientCharacterId)] as JsonObject)?["statuses"] as JsonObject;
            if (mailboxStatuses is not null)
            {
                foreach (var (statusKey, _) in mailboxStatuses)
                    if (messages[statusKey] is JsonObject existing &&
                        Number(existing["welcomeMailForCharacterID"]) == recipientCharacterId &&
                        Number(existing["senderID"]) == senderId)
                        return Number(existing["messageID"]);
            }

            var messageId = NextMailId(messages, mailboxes);
            var sentFiletime = DateTimeOffset.FromUnixTimeMilliseconds(nowUnixMs()).UtcDateTime.ToFileTimeUtc();
            var messageKey = Key(messageId);
            var message = new JsonObject
            {
                ["messageID"] = messageId,
                ["senderID"] = senderId,
                ["senderName"] = senderName,
                ["toCharacterIDs"] = LongArray([recipientCharacterId]),
                ["toListID"] = null,
                ["toCorpOrAllianceID"] = null,
                ["title"] = title,
                ["subject"] = title,
                ["body"] = body,
                ["sentDate"] = sentFiletime,
                ["replyToMessageID"] = null,
                ["forwardedFromMessageID"] = null,
                ["statusMask"] = 0,
                ["labelMask"] = 0,
                ["read"] = false,
                ["trashed"] = false,
                ["replied"] = false,
                ["forwarded"] = false,
                ["welcomeMailForCharacterID"] = recipientCharacterId,
            };
            var status = new JsonObject
            {
                ["statusMask"] = 0,
                ["labelMask"] = 1,
            };

            RequireWrite("mail", $"/messages/{messageKey}", message);
            RequireWrite("mail", $"/mailboxes/{Key(recipientCharacterId)}/statuses/{messageKey}", status);
            await FlushUntilVisibleAsync("mail", persisted =>
                persisted["messages"] is JsonObject persistedMessages &&
                persistedMessages[messageKey] is JsonObject savedMessage &&
                Number(savedMessage["senderID"]) == senderId &&
                Number(savedMessage["welcomeMailForCharacterID"]) == recipientCharacterId &&
                persisted["mailboxes"] is JsonObject persistedMailboxes &&
                persistedMailboxes[Key(recipientCharacterId)] is JsonObject mailbox &&
                mailbox["statuses"] is JsonObject savedStatuses &&
                savedStatuses[messageKey] is JsonObject savedStatus &&
                Number(savedStatus["statusMask"]) == 0 &&
                Number(savedStatus["labelMask"]) == 1, cancellationToken);

            var notifications = readCacheTable("notifications");
            var notificationBoxes = notifications["boxes"] as JsonObject ?? new JsonObject();
            var notificationId = NextNotificationId(notificationBoxes);
            var notification = new JsonObject
            {
                ["notificationID"] = notificationId,
                ["typeID"] = 1004,
                ["senderID"] = senderId,
                ["receiverID"] = recipientCharacterId,
                ["processed"] = false,
                ["created"] = sentFiletime,
                ["data"] = new JsonObject
                {
                    ["senderName"] = senderName,
                    ["subject"] = title,
                    ["msg"] = (JsonObject)message.DeepClone(),
                },
            };
            RequireWrite("notifications", $"/boxes/{Key(recipientCharacterId)}/byID/{Key(notificationId)}", notification);
            await FlushUntilVisibleAsync("notifications", persisted =>
                persisted["boxes"] is JsonObject boxes &&
                boxes[Key(recipientCharacterId)] is JsonObject recipientBox &&
                recipientBox["byID"] is JsonObject records &&
                records[Key(notificationId)] is JsonObject saved &&
                Number(saved["typeID"]) == 1004 &&
                Number(saved["receiverID"]) == recipientCharacterId, cancellationToken);

            sendNotification(recipientCharacterId,
            [
                new Eve.Marshal.PyLong(notificationId),
                new Eve.Marshal.PyInt(1004),
                new Eve.Marshal.PyLong(senderId),
                Py.FileTimeLong(sentFiletime),
                NotificationData(senderId, senderName, title, message, sentFiletime),
            ]);
            return messageId;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>Persists and delivers a kill-right notification through the shared notification mailbox.</summary>
    public Task WriteKillRightNotificationAsync(
        long recipientId,
        long senderId,
        int typeId,
        JsonObject data,
        string? idempotencyKey,
        CancellationToken cancellationToken) =>
        WriteNotificationAsync(recipientId, senderId, typeId,
            Eve.Services.Crimewatch.KillRightNotificationTypes.MiscGroup,
            data, idempotencyKey, cancellationToken);

    public Task WriteAllianceMaintenanceBillNotificationAsync(
        long recipientId, long allianceId, long billId, CancellationToken cancellationToken)
    {
        if (recipientId <= 0 || allianceId <= 0 || billId <= 0)
            return Task.CompletedTask;
        return WriteNotificationAsync(recipientId, 1_000_125, 4, 2,
            new JsonObject { ["allianceID"] = allianceId },
            $"alliance-maintenance-bill:{billId}:receiver:{recipientId}", cancellationToken);
    }

    /// <summary>The located notification (type 69, group agents), sent by the agent; the key keeps one locate to one notification.</summary>
    public Task WriteLocateNotificationAsync(long recipientId, long agentId, JsonObject data, string idempotencyKey, CancellationToken cancellationToken) =>
        WriteNotificationAsync(recipientId, agentId, 69, 1, data, idempotencyKey, cancellationToken);

    private async Task WriteNotificationAsync(
        long recipientId, long senderId, int typeId, int groupId, JsonObject data,
        string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (recipientId <= 0 || senderId <= 0 || typeId <= 0 || groupId < 0)
            return;
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            var boxes = readCacheTable("notifications")["boxes"] as JsonObject ?? new JsonObject();
            var recipientKey = Key(recipientId);
            if (idempotencyKey is not null &&
                (boxes[recipientKey] as JsonObject)?["byID"] is JsonObject existingRows &&
                existingRows.Any(entry => entry.Value is JsonObject existing &&
                    string.Equals(Text(existing["idempotencyKey"]), idempotencyKey, StringComparison.Ordinal)))
            {
                return;
            }

            var notificationId = NextNotificationId(boxes);
            var created = DateTimeOffset.FromUnixTimeMilliseconds(nowUnixMs()).UtcDateTime.ToFileTimeUtc();
            var notification = new JsonObject
            {
                ["notificationID"] = notificationId,
                ["typeID"] = typeId,
                ["senderID"] = senderId,
                ["receiverID"] = recipientId,
                ["groupID"] = groupId,
                ["processed"] = false,
                ["created"] = created,
                ["data"] = data.DeepClone(),
            };
            if (idempotencyKey is not null)
                notification["idempotencyKey"] = idempotencyKey;
            RequireWrite("notifications", $"/boxes/{recipientKey}/byID/{Key(notificationId)}", notification);
            await FlushUntilVisibleAsync("notifications", persisted =>
            {
                var saved = (persisted["boxes"] as JsonObject)?[recipientKey] is JsonObject box &&
                    box["byID"] is JsonObject rows ? rows[Key(notificationId)] as JsonObject : null;
                return saved is not null && Number(saved["typeID"]) == typeId &&
                    Number(saved["senderID"]) == senderId && Number(saved["receiverID"]) == recipientId &&
                    Number(saved["groupID"]) == groupId;
            }, cancellationToken);
            sendNotification(recipientId,
            [
                new Eve.Marshal.PyLong(notificationId),
                new Eve.Marshal.PyInt(typeId),
                new Eve.Marshal.PyLong(senderId),
                Py.FileTimeLong(created),
                NotificationWireData(data),
            ]);
        }
        finally
        {
            _sendGate.Release();
        }
    }
    private void RequireWrite(string table, string path, JsonNode data)
    {
        var result = write(table, path, data);
        if (!result.Success)
            throw new InvalidOperationException($"Could not persist {table}{path}: {result.ErrorMsg}");
    }

    private async Task FlushUntilVisibleAsync(
        string table,
        Func<JsonObject, bool> isVisible,
        CancellationToken cancellationToken)
    {
        var delayMs = 10;
        while (true)
        {
            await flushTableAsync(table, cancellationToken);
            if (isVisible(await readPersistedTableAsync(table, cancellationToken)))
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken);
            delayMs = Math.Min(delayMs * 2, 250);
        }
    }

    private static long NextMailId(JsonObject messages, JsonObject mailboxes)
    {
        var maximum = MaximumNumericKey(messages);
        foreach (var (_, mailboxNode) in mailboxes)
            if (mailboxNode is JsonObject mailbox && mailbox["statuses"] is JsonObject statuses)
                maximum = Math.Max(maximum, MaximumNumericKey(statuses));
        return checked(maximum + 1);
    }

    private static long NextNotificationId(JsonObject boxes)
    {
        var maximum = 0L;
        foreach (var (_, boxNode) in boxes)
            if (boxNode is JsonObject box && box["byID"] is JsonObject records)
                maximum = Math.Max(maximum, MaximumNumericKey(records));
        return checked(maximum + 1);
    }

    private static long MaximumNumericKey(JsonObject rows)
    {
        var maximum = 0L;
        foreach (var (key, _) in rows)
            if (long.TryParse(key, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var id) && id > maximum)
                maximum = id;
        return maximum;
    }

    private static long Number(JsonNode? value) =>
        long.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : 0;

    private static string Text(JsonNode? value) => value?.ToString() ?? "";

    private static string Key(long id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static JsonArray LongArray(IEnumerable<long> values)
    {
        var result = new JsonArray();
        foreach (var value in values)
            result.Add(value);
        return result;
    }

    private static JsonNode? OptionalLong(long? value) => value is { } id ? JsonValue.Create(id) : null;

    private static Eve.Marshal.PyValue NotificationWireData(JsonObject data) =>
        new Eve.Marshal.PyDict([.. data.Select(pair => new KeyValuePair<Eve.Marshal.PyValue, Eve.Marshal.PyValue>(
            new Eve.Marshal.PyStr(pair.Key), NotificationWireValue(pair.Value)))]);

    private static Eve.Marshal.PyValue NotificationWireValue(JsonNode? value)
    {
        // a nested object keyed by group ids (the located notification's places) is a dict with integer keys
        if (value is JsonObject nested)
        {
            return new Eve.Marshal.PyDict([.. nested.Select(pair => new KeyValuePair<Eve.Marshal.PyValue, Eve.Marshal.PyValue>(
                long.TryParse(pair.Key, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var key)
                    ? new Eve.Marshal.PyLong(key)
                    : new Eve.Marshal.PyStr(pair.Key),
                NotificationWireValue(pair.Value)))]);
        }
        if (value is not JsonValue scalar)
            return Eve.Marshal.PyNone.Instance;
        if (scalar.TryGetValue<long>(out var integer))
            return new Eve.Marshal.PyLong(integer);
        if (scalar.TryGetValue<decimal>(out var amount))
            return new Eve.Marshal.PyReal((double)amount);
        if (scalar.TryGetValue<double>(out var number) && double.IsFinite(number))
            return new Eve.Marshal.PyReal(number);
        if (scalar.TryGetValue<bool>(out var flag))
            return flag ? Eve.Marshal.PyBool.True : Eve.Marshal.PyBool.False;
        if (scalar.TryGetValue<string>(out var text))
            return new Eve.Marshal.PyStr(text);
        return Eve.Marshal.PyNone.Instance;
    }
    private static Eve.Marshal.PyValue NotificationData(
        long senderId,
        string senderName,
        string subject,
        JsonObject message,
        long sentFiletime)
    {
        var messageView = Py.KeyVal(
            ("messageID", new Eve.Marshal.PyLong(Number(message["messageID"]))),
            ("senderID", new Eve.Marshal.PyLong(senderId)),
            ("senderName", new Eve.Marshal.PyStr(senderName)),
            ("sentDate", Py.FileTimeLong(sentFiletime)),
            ("toCharacterIDs", new Eve.Marshal.PyList(
                ((JsonArray)message["toCharacterIDs"]!).Select(value => (Eve.Marshal.PyValue)new Eve.Marshal.PyLong(Number(value))).ToArray())),
            ("toListID", message["toListID"] is null ? Eve.Marshal.PyNone.Instance : new Eve.Marshal.PyLong(Number(message["toListID"]))),
            ("toCorpOrAllianceID", message["toCorpOrAllianceID"] is null
                ? Eve.Marshal.PyNone.Instance
                : new Eve.Marshal.PyList(((JsonArray)message["toCorpOrAllianceID"]!)
                    .Select(value => (Eve.Marshal.PyValue)new Eve.Marshal.PyLong(Number(value))).ToArray())),
            ("subject", new Eve.Marshal.PyStr(subject)),
            ("statusMask", new Eve.Marshal.PyInt(0)),
            ("labelMask", new Eve.Marshal.PyInt(0)),
            ("read", Eve.Marshal.PyBool.False),
            ("trashed", Eve.Marshal.PyBool.False),
            ("replied", Eve.Marshal.PyBool.False),
            ("forwarded", Eve.Marshal.PyBool.False));
        return Py.KeyVal(
            ("senderName", new Eve.Marshal.PyStr(senderName)),
            ("subject", new Eve.Marshal.PyStr(subject)),
            ("msg", messageView));
    }
}
