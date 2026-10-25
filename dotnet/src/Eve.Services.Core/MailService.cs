using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Core;

/// <summary>

/// server/src/services/mail/{mailMgrService,mailState}.js.
/// Client: eve-logic 1dbb5711a072a146e1d228365668ac05655eefe1,
/// eve/client/script/ui/services/mail/mailSvc.py (SyncMail, GetBody, GetLabels and mailbox mutations).
/// </summary>
/// <param name="readMail">Returns a consistent snapshot of the persisted mail table root.</param>
/// <param name="markAsRead">
/// Rechecks mailbox membership and persists the read bit atomically, preserving other bits.
/// Completes after commit and any OnMailUpdated announcement to other sessions.
/// </param>
public sealed class MailService(
    Func<CancellationToken, Task<JsonObject>> readMail,
    Func<long, long, ClientSession, CancellationToken, Task> markAsRead,
    Func<MailSendRequest, ClientSession, CancellationToken, Task<long?>>? sendMail = null,
    GmChatPokeMailCallback? pokePlayerAboutChatMsgGm = null,
    MailboxMutationCallback? mutateMailbox = null) : IService
{
    private readonly Func<CancellationToken, Task<JsonObject>> _readMail =
        readMail ?? throw new ArgumentNullException(nameof(readMail));
    private readonly Func<long, long, ClientSession, CancellationToken, Task> _markAsRead =
        markAsRead ?? throw new ArgumentNullException(nameof(markAsRead));
    private readonly Func<MailSendRequest, ClientSession, CancellationToken, Task<long?>>? _sendMail = sendMail;
    private readonly GmChatPokeMailCallback? _pokePlayerAboutChatMsgGm = pokePlayerAboutChatMsgGm;
    private readonly MailboxMutationCallback? _mutateMailbox = mutateMailbox;

    private const int MaxCharacterRecipients = 50;
    private const int MaxMailboxMessageIds = 500;
    private const int MaxMailboxSyncEntries = 5_000;
    private const int MaxSubjectCharacters = 150;
    private const int MaxBodyCharacters = 8000;
    private const int MaxGmChatChannelCharacters = 256;

    public string Name => "mailMgr";

    public bool HasMethod(string method) => method is
        "SyncMail" or "GetMailHeaders" or "GetBody" or "GetLabels" or "MarkAsRead" or
        "MarkAsUnread" or "MarkAsReadByLabel" or "MarkAsReadByList" or
        "MarkAsUnreadByLabel" or "MarkAsUnreadByList" or "MoveFromTrash" or "MoveToTrash" or
        "MoveAllFromTrash" or "MoveAllToTrash" or "MoveToTrashByLabel" or "MoveToTrashByList" or
        "RemoveLabels" or "ReplaceLabels" or "SendMail" or "PokePlayerAboutChatMsgGm";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method))
            return PyNone.Instance;
        call.Cancellation.ThrowIfCancellationRequested();

        if (call.Method == "MarkAsRead" && _mutateMailbox is not null)
        {
            await MutateMailboxAsync(call);
            return PyNone.Instance;
        }
        if (call.Method is "MarkAsUnread" or "MarkAsReadByLabel" or "MarkAsReadByList" or
            "MarkAsUnreadByLabel" or "MarkAsUnreadByList" or "MoveFromTrash" or "MoveToTrash" or
            "MoveAllFromTrash" or "MoveAllToTrash" or "MoveToTrashByLabel" or "MoveToTrashByList" or
            "RemoveLabels" or "ReplaceLabels")
        {
            await MutateMailboxAsync(call);
            return PyNone.Instance;
        }
        if (call.Method == "MarkAsRead")
        {
            var requestedIds = Items(call.Arg(0));
            if (requestedIds.Count > MaxMailboxMessageIds)
                throw MailLimitRefusal("MAIL_BULK_LIMIT_EXCEEDED");
            var requestingCharacterId = call.Session.CharacterId.GetValueOrDefault();
            if (requestingCharacterId > 0)
                foreach (var id in requestedIds.Select(PositiveId).Where(id => id > 0).Distinct())
                    await _markAsRead(requestingCharacterId, id, call.Session, call.Cancellation);
            return PyNone.Instance;
        }
        if (call.Method == "SendMail")
            return await SendMailAsync(call);
        if (call.Method == "PokePlayerAboutChatMsgGm")
            return await PokePlayerAboutChatMsgGmAsync(call);

        var characterId = call.Session.CharacterId.GetValueOrDefault();
        var state = characterId > 0 ? await _readMail(call.Cancellation) : new JsonObject();
        var mailbox = (state["mailboxes"] as JsonObject)?[Key(characterId)] as JsonObject;
        var statuses = mailbox?["statuses"] as JsonObject;
        var messages = state["messages"] as JsonObject;

        switch (call.Method)
        {
            case "SyncMail":
                return SyncMail(messages, statuses, call.Arg(0), call.Arg(1));
            case "GetMailHeaders":
            {
                var requestedIds = Items(call.Arg(0));
                if (requestedIds.Count > MaxMailboxMessageIds)
                    throw MailLimitRefusal("MAIL_BULK_LIMIT_EXCEEDED");
                var headers = new List<PyValue>();
                foreach (var id in requestedIds.Select(PositiveId).Where(id => id > 0).Distinct())
                    if (Entry(messages, statuses, id) is { } entry)
                        headers.Add(Header(entry.Message));
                return new PyList(headers);
            }
            case "GetLabels":
                return Labels(mailbox?["labels"] as JsonObject);
            case "GetBody":
            {
                var id = PositiveId(call.Arg(0));
                if (id <= 0 || Entry(messages, statuses, id) is not { } entry)
                    return PyNone.Instance;
                var mark = JsSemantics.ToNumber(call.Arg(1));
                if (double.IsFinite(mark) && mark != 0 && (Integer(entry.Status["statusMask"]) & 1) == 0)
                {
                    await _markAsRead(characterId, id, call.Session, call.Cancellation);
                    // Re-read after the write: a concurrent delete must not resurrect a body.
                    state = await _readMail(call.Cancellation);
                    mailbox = (state["mailboxes"] as JsonObject)?[Key(characterId)] as JsonObject;
                    if (Entry(state["messages"] as JsonObject, mailbox?["statuses"] as JsonObject, id) is not { } current)
                        return PyNone.Instance;
                    entry = current;
                }
                using var output = new MemoryStream();
                using (var compressor = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
                    compressor.Write(Encoding.UTF8.GetBytes(Text(entry.Message["body"])));
                return new PyBuffer(output.ToArray());
            }
            default:
                return PyNone.Instance;
        }
    }

private async Task MutateMailboxAsync(ServiceCall call)
    {
        var characterId = call.Session.CharacterId.GetValueOrDefault();
        if (characterId <= 0)
            return;
        var callback = _mutateMailbox ??
            throw new InvalidOperationException("mailMgr mailbox changes require an atomic persistence callback.");
        if (call.Method is "MarkAsRead" or "MarkAsUnread" or "MoveFromTrash" or "MoveToTrash" or "RemoveLabels" or "ReplaceLabels")
        {
            var ids = Items(call.Arg(0));
            if (ids.Count > MaxMailboxMessageIds)
                throw MailLimitRefusal("MAIL_BULK_LIMIT_EXCEEDED");
            if (ids.Any(id => PositiveId(id) <= 0))
                throw MailLimitRefusal("MAIL_MESSAGE_ID_INVALID");
        }
        if (call.Method is "MarkAsReadByLabel" or "MarkAsUnreadByLabel" or "MoveToTrashByLabel")
        {
            if (PositiveId(call.Arg(0)) <= 0)
                throw MailLimitRefusal("MAIL_LABEL_ID_INVALID");
        }
        if (call.Method is "MarkAsReadByList" or "MarkAsUnreadByList" or "MoveToTrashByList")
        {
            if (PositiveId(call.Arg(0)) <= 0)
                throw MailLimitRefusal("MAIL_LIST_ID_INVALID");
        }
        if (call.Method is "RemoveLabels" or "ReplaceLabels")
        {
            var labels = NonNegativeId(call.Arg(1));
            if (labels < 0)
                throw MailLimitRefusal("MAIL_LABEL_ID_INVALID");
        }
        await callback(new MailboxMutationRequest(characterId, call.Method, call.Args, call.Session), call.Cancellation);
    }

    private static long NonNegativeId(PyValue value) => value switch
    {
        PyInt integer when integer.Value >= 0 => integer.Value,
        PyLong integer when integer.Value >= 0 => integer.Value,
        PyReal real when double.IsFinite(real.Value) && real.Value >= 0 && real.Value < long.MaxValue
            && real.Value == Math.Truncate(real.Value) => (long)real.Value,
        _ => -1,
    };

    private async ValueTask<PyValue> SendMailAsync(ServiceCall call)
    {
        var characterId = call.Session.CharacterId.GetValueOrDefault();
        if (characterId <= 0)
            return PyNone.Instance;
        if (_sendMail is null)
            throw new InvalidOperationException("mailMgr.SendMail requires a durable persistence callback.");

        var title = BoundedText(call.Arg(3), MaxSubjectCharacters, "MAIL_SUBJECT_LIMIT_EXCEEDED");
        var body = BoundedText(call.Arg(4), MaxBodyCharacters, "MAIL_BODY_LIMIT_EXCEEDED");
        var request = new MailSendRequest(
            characterId,
            RecipientIds(call.Arg(0)),
            OptionalPositiveId(call.Arg(1)),
            OptionalRecipientIds(call.Arg(2)),
            title,
            body,
            OptionalPositiveId(call.Arg(5)),
            OptionalPositiveId(call.Arg(6)));
        var messageId = await _sendMail(request, call.Session, call.Cancellation);
        return messageId is > 0 ? new PyLong(messageId.Value) : PyNone.Instance;
    }

    private async ValueTask<PyValue> PokePlayerAboutChatMsgGmAsync(ServiceCall call)
    {
        if (!Eve.Services.GameMasterRoles.Has(call.Session, Eve.Services.GameMasterRoles.Gml))
            return PyNone.Instance;

        var characterId = PositiveId(call.Arg(0));
        if (characterId <= 0 || _pokePlayerAboutChatMsgGm is null)
            return PyNone.Instance;

        var channelName = call.Arg(1) is PyNone
            ? "Unknown Channel"
            : BoundedText(call.Arg(1), MaxGmChatChannelCharacters, "MAIL_CHANNEL_LIMIT_EXCEEDED");
        var request = new GmChatPokeMailRequest(
            characterId,
            GmChatPokeMailRequest.SystemSenderId,
            channelName,
            "GM chat request",
            $"A GM would like to speak with you.<br><br>Requested channel: {channelName}",
            SaveSenderCopy: false);
        var messageId = await _pokePlayerAboutChatMsgGm(request, call.Session, call.Cancellation);
        return messageId is > 0 ? new PyLong(messageId.Value) : PyNone.Instance;
    }

    private static IReadOnlyList<long> RecipientIds(PyValue value)
    {
        IReadOnlyList<PyValue> values = value switch
        {
            PyList list => list.Items,
            PyTuple tuple => tuple.Items,
            PyNone => [],
            _ => [value],
        };
        if (values.Count > MaxCharacterRecipients)
            throw MailLimitRefusal("MAIL_RECIPIENT_LIMIT_EXCEEDED");
        var ids = new List<long>(values.Count);
        foreach (var recipientValue in values)
        {
            var id = recipientValue switch
            {
                PyInt integer when integer.Value > 0 => integer.Value,
                PyLong integer when integer.Value > 0 => integer.Value,
                _ => 0
            };
            if (id <= 0)
                throw MailLimitRefusal("MAIL_RECIPIENT_INVALID");
            ids.Add(id);
        }
        return ids.Distinct().ToArray();
    }

private static IReadOnlyList<long>? OptionalRecipientIds(PyValue value) =>
        value is PyNone ? null : RecipientIds(value);

    private static long? OptionalPositiveId(PyValue value) =>
        PositiveId(value) is > 0 and var id ? id : null;

    private static string StringArgument(PyValue value) => value switch
    {
        PyStr text => text.Value,
        PyBytes bytes => Encoding.UTF8.GetString(bytes.Value.Span),
        _ => string.Empty,
    };

    private static PyValue SyncMail(JsonObject? messages, JsonObject? statuses, PyValue first, PyValue last)
    {
        if (statuses?.Count > MaxMailboxSyncEntries)
            throw MailLimitRefusal("MAIL_SYNC_LIMIT_EXCEEDED");
        var entries = new List<(JsonObject Message, JsonObject Status)>();
        if (statuses is not null && messages is not null)
            foreach (var (key, status) in statuses)
                if (status is JsonObject row && messages[key] is JsonObject message)
                    entries.Add((message, row));
        entries.Sort((left, right) =>
        {
            var byDate = FileTime(right.Message["sentDate"]).CompareTo(FileTime(left.Message["sentDate"]));
            return byDate != 0 ? byDate : Integer(right.Message["messageID"]).CompareTo(Integer(left.Message["messageID"]));
        });

        var firstId = PositiveId(first);
        var lastId = PositiveId(last);
        var all = first is PyNone || lastId == 0;
        var newMail = new List<PyValue>();
        var oldMail = new List<PyValue>();
        var mailStatus = new List<PyValue>();
        foreach (var (message, status) in entries)
        {
            var id = Math.Max(0, Integer(message["messageID"]));
            mailStatus.Add(Py.KeyVal(
                ("messageID", new PyInt(id)),
                ("statusMask", new PyInt(Integer(status["statusMask"]))),
                ("labelMask", new PyInt(Integer(status["labelMask"])))));
            if (all || id > lastId)
                newMail.Add(Header(message));
            else if (id < firstId)
                oldMail.Add(Header(message));
        }
        return Py.KeyVal(
            ("newMail", new PyList(newMail)),
            ("oldMail", new PyList(oldMail)),
            ("mailStatus", new PyList(mailStatus)));
    }

    private static (JsonObject Message, JsonObject Status)? Entry(JsonObject? messages, JsonObject? statuses, long id) =>
        messages?[Key(id)] is JsonObject message && statuses?[Key(id)] is JsonObject status
            ? (message, status)
            : null;

    private static PyValue Header(JsonObject message)
    {
        var recipients = message["toCharacterIDs"] as JsonArray;
        return Py.KeyVal(
            ("messageID", new PyInt(Math.Max(0, Integer(message["messageID"])))),
            ("senderID", new PyInt(Math.Max(0, Integer(message["senderID"])))),
            ("toCharacterIDs", recipients is { Count: > 0 }
                ? new PyStr(string.Join(",", recipients.Select(Text))) : PyNone.Instance),
            ("toListID", OptionalId(message["toListID"])),
            ("toCorpOrAllianceID", OptionalIds(message["toCorpOrAllianceID"])),
            ("title", new PyStr(Text(message["title"]))),
            ("sentDate", new PyLong(FileTime(message["sentDate"]))));
    }

    private static PyValue Labels(JsonObject? labels) => new PyDict(labels is null ? [] :
        [.. labels.OrderBy(pair => long.TryParse(pair.Key, CultureInfo.InvariantCulture, out var id) ? id : 0)
            .Select(pair =>
            {
                var id = long.TryParse(pair.Key, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
                var label = pair.Value as JsonObject;
                return new KeyValuePair<PyValue, PyValue>(new PyInt(id), Py.KeyVal(
                    ("labelID", new PyInt(Math.Max(0, id))),
                    ("name", new PyStr(Text(label?["name"]))),
                    ("color", new PyInt(Integer(label?["color"])))));
            })]);

    private static PyValue OptionalId(JsonNode? value) =>
        value is null ? PyNone.Instance : new PyInt(Math.Max(0, Integer(value)));

private static PyValue OptionalIds(JsonNode? value) => value is JsonArray ids
        ? new PyList([.. ids.Select(Integer).Where(id => id > 0).Select(id => (PyValue)new PyInt(id))])
        : OptionalId(value);

    private static string BoundedText(PyValue value, int maximumCharacters, string refusalCode)
    {
        var text = StringArgument(value);
        var characters = 0;
        foreach (var _ in text.EnumerateRunes())
            if (++characters > maximumCharacters)
                throw MailLimitRefusal(refusalCode);
        return text;
    }

    private static MachoWrappedException MailLimitRefusal(string refusalCode) =>
        MachoErrors.ThrowUserError("CustomInfo", ("info", new PyStr(refusalCode)));

    private static IReadOnlyList<PyValue> Items(PyValue value) => value switch
    {
        PyList list => list.Items,
        PyTuple tuple => tuple.Items,
        _ => [],
    };

    private static string Key(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static long PositiveId(PyValue value) => value switch
    {
        PyInt integer when integer.Value > 0 => integer.Value,
        PyLong integer when integer.Value is > 0 and < long.MaxValue => integer.Value,
        PyReal real when double.IsFinite(real.Value) && real.Value >= 1 && real.Value < long.MaxValue
            && real.Value == Math.Truncate(real.Value) => (long)real.Value,
        _ => 0,
    };

    private static long Integer(JsonNode? value)
    {
        if (value is JsonValue scalar && scalar.TryGetValue<bool>(out var boolean))
            return boolean ? 1 : 0;
        return double.TryParse(Text(value), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && double.IsFinite(number) && number > long.MinValue && number < long.MaxValue
            ? (long)Math.Truncate(number) : 0;
    }

    // Never round FILETIME through double or synthesize a current date for missing persisted data.
    private static long FileTime(JsonNode? value) =>
        long.TryParse(Text(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var time) ? time : 0;

    private static string Text(JsonNode? value) =>
        value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : value?.ToJsonString() ?? "";
}

public sealed record MailboxMutationRequest(
    long CharacterId,
    string Method,
    IReadOnlyList<PyValue> Arguments,
    ClientSession Session);

public delegate Task MailboxMutationCallback(
    MailboxMutationRequest request,
    CancellationToken cancellationToken);

public sealed record MailboxMutationResult(
    IReadOnlyList<(long Id, long StatusMask, long LabelMask)> Changed);

public static class MailboxMutation
{
    public static MailboxMutationResult Apply(
        JsonObject mailbox,
        JsonObject messages,
        MailboxMutationRequest request)
    {
        var statuses = mailbox["statuses"] as JsonObject;
        if (statuses is null)
            return new MailboxMutationResult([]);
        var arguments = request.Arguments;
        var requestedIds = arguments.Count > 0 ? Ids(arguments[0]).ToHashSet() : [];
        var selector = arguments.Count > 0 ? Integer(arguments[0]) : 0;
        var labelMask = arguments.Count > 1 ? Integer(arguments[1]) : 0;
        var changed = new List<(long Id, long StatusMask, long LabelMask)>();
        foreach (var (key, node) in statuses.ToArray())
        {
            if (node is not JsonObject status ||
                !long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ||
                id <= 0 || messages[key] is not JsonObject message)
                continue;
            var statusMask = Integer(status["statusMask"]);
            var currentLabels = Integer(status["labelMask"]);
            var selected = request.Method switch
            {
                "MarkAsRead" or "MarkAsUnread" or "MoveFromTrash" or "MoveToTrash" =>
                    requestedIds.Contains(id),
                "MoveAllToTrash" or "MoveAllFromTrash" => true,
                "MarkAsReadByLabel" or "MarkAsUnreadByLabel" or "MoveToTrashByLabel" =>
                    (currentLabels & selector) == selector,
                "MarkAsReadByList" or "MarkAsUnreadByList" or "MoveToTrashByList" =>
                    Integer(message["toListID"]) == selector,
                "RemoveLabels" or "ReplaceLabels" => requestedIds.Contains(id),
                _ => false,
            };
            if (!selected)
                continue;
            if (request.Method is "MarkAsReadByLabel" or "MarkAsUnreadByLabel" or
                "MarkAsReadByList" or "MarkAsUnreadByList")
            {
                if ((statusMask & 8) != 0)
                    continue;
            }
            var nextStatus = request.Method switch
            {
                "MarkAsRead" or "MarkAsReadByLabel" or "MarkAsReadByList" => statusMask | 1,
                "MarkAsUnread" or "MarkAsUnreadByLabel" or "MarkAsUnreadByList" => statusMask & ~1L,
                "MoveToTrash" or "MoveAllToTrash" or "MoveToTrashByLabel" or "MoveToTrashByList" => statusMask | 8,
                "MoveFromTrash" or "MoveAllFromTrash" => statusMask & ~8L,
                _ => statusMask,
            };
            var nextLabels = request.Method switch
            {
                "RemoveLabels" => currentLabels & ~labelMask,
                "ReplaceLabels" => labelMask,
                _ => currentLabels,
            };
            if (nextStatus == statusMask && nextLabels == currentLabels)
                continue;
            status["statusMask"] = nextStatus;
            status["labelMask"] = nextLabels;
            changed.Add((id, nextStatus, nextLabels));
        }
        return new MailboxMutationResult(changed);
    }

    private static IReadOnlyList<long> Ids(PyValue value) => value switch
    {
        PyList list => list.Items.Select(Integer).Where(id => id > 0).Distinct().ToArray(),
        PyTuple tuple => tuple.Items.Select(Integer).Where(id => id > 0).Distinct().ToArray(),
        _ => [],
    };

    private static long Integer(PyValue value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        PyReal real when double.IsFinite(real.Value) && real.Value >= long.MinValue && real.Value < long.MaxValue
            && real.Value == Math.Truncate(real.Value) => (long)real.Value,
        _ => 0,
    };

    private static long Integer(JsonNode? value) =>
        long.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number : 0;
}

public sealed record MailSendRequest(
    long SenderCharacterId,
    IReadOnlyList<long> CharacterRecipientIds,
    long? MailingListId,
    IReadOnlyList<long>? CorporationOrAllianceRecipientIds,
    string Title,
    string Body,
    long? ReplyToMessageId,
    long? ForwardedFromMessageId,
    bool SaveSenderCopy = true);

public sealed record GmChatPokeMailRequest(
    long CharacterId,
    long SenderCharacterId,
    string ChannelName,
    string Title,
    string Body,
    bool SaveSenderCopy)
{
    public const long SystemSenderId = 140000004;
    public const string SystemSenderName = "GM ELYSIAN";
}

public delegate Task<long?> GmChatPokeMailCallback(
    GmChatPokeMailRequest request,
    ClientSession session,
    CancellationToken cancellationToken);
