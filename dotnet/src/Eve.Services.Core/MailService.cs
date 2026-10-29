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
    private const int MaxSubjectCharacters = 150;
    private const int MaxBodyCharacters = 8000;

    public string Name => "mailMgr";

    public bool HasMethod(string method) => method is
        "SyncMail" or "GetMailHeaders" or "GetBody" or "GetLabels" or
        "MarkAsRead" or "MarkAsUnread" or "MarkAllAsRead" or "MarkAllAsUnread" or
        "MarkAsReadByLabel" or "MarkAsReadByList" or "MarkAsUnreadByLabel" or "MarkAsUnreadByList" or
        "MoveFromTrash" or "MoveToTrash" or "MoveAllFromTrash" or "MoveAllToTrash" or
        "MoveToTrashByLabel" or "MoveToTrashByList" or "EmptyTrash" or "DeleteMail" or
        "RemoveLabels" or "ReplaceLabels" or "AssignLabels" or
        "CreateLabel" or "EditLabel" or "DeleteLabel" or
        "SendMail" or "PokePlayerAboutChatMsgGm";

    private static bool IsMailboxMutation(string method) => method is
        "MarkAsRead" or "MarkAsUnread" or "MarkAllAsRead" or "MarkAllAsUnread" or
        "MarkAsReadByLabel" or "MarkAsReadByList" or "MarkAsUnreadByLabel" or "MarkAsUnreadByList" or
        "MoveFromTrash" or "MoveToTrash" or "MoveAllFromTrash" or "MoveAllToTrash" or
        "MoveToTrashByLabel" or "MoveToTrashByList" or "EmptyTrash" or "DeleteMail" or
        "AssignLabels" or "RemoveLabels" or "ReplaceLabels" or
        "CreateLabel" or "EditLabel" or "DeleteLabel";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method))
            return PyNone.Instance;
        call.Cancellation.ThrowIfCancellationRequested();

        if (IsMailboxMutation(call.Method) && (call.Method != "MarkAsRead" || _mutateMailbox is not null))
        {
            var result = await MutateMailboxAsync(call);
            return call.Method == "CreateLabel" && result.CreatedLabelId is > 0 and var labelId
                ? new PyInt(labelId)
                : PyNone.Instance;
        }
        if (call.Method == "MarkAsRead")
        {
            var requestedIds = Items(call.Arg(0));
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

    private async Task<MailboxMutationResult> MutateMailboxAsync(ServiceCall call)
    {
        var characterId = call.Session.CharacterId.GetValueOrDefault();
        if (characterId <= 0)
            return MailboxMutationResult.Empty;
        var callback = _mutateMailbox ??
            throw new InvalidOperationException("mailMgr mailbox changes require an atomic persistence callback.");
        return await callback(
            new MailboxMutationRequest(characterId, call.Method, call.Args, call.Session),
            call.Cancellation);
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

        var title = NormalizeMailText(call.Arg(3), MaxSubjectCharacters);
        var body = NormalizeMailText(call.Arg(4), MaxBodyCharacters);
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

        var channelName = JsSemantics.StringValue(call.Arg(1)) ?? "Unknown Channel";
        var request = new GmChatPokeMailRequest(
            characterId,
            GmChatPokeMailRequest.SystemSenderId,
            channelName,
            "GM chat request",
            GmChatPokeMailRequest.BuildBody(channelName),
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

private static IReadOnlyList<long>? OptionalRecipientIds(PyValue value)
    {
        var ids = RecipientIds(value);
        return ids.Count == 0 ? null : ids;
    }

    private static long? OptionalPositiveId(PyValue value) =>
        PositiveId(value) is > 0 and var id ? id : null;

    private static string StringArgument(PyValue value) =>
        JsSemantics.StringValue(value) ?? string.Empty;

    private static PyValue SyncMail(JsonObject? messages, JsonObject? statuses, PyValue first, PyValue last)
    {
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

    private static PyValue OptionalIds(JsonNode? value)
    {
        if (value is not JsonArray ids)
            return OptionalId(value);
        var id = ids.Select(Integer).FirstOrDefault(candidate => candidate > 0);
        return id > 0 ? new PyInt(id) : PyNone.Instance;
    }

    private static string NormalizeMailText(PyValue value, int maximumCharacters)
    {
        var text = StringArgument(value).Trim();
        return text.Length > maximumCharacters ? text[..maximumCharacters] : text;
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

public static class MailSentNotification
{
    public static PyValue[] Build(
        MailSendRequest request,
        long messageId,
        long sentFiletime,
        long statusMask)
    {
        var organizationId = request.CorporationOrAllianceRecipientIds?.FirstOrDefault();
        return
        [
            new PyLong(messageId),
            new PyLong(request.SenderCharacterId),
            new PyLong(sentFiletime),
            new PyList([.. request.CharacterRecipientIds.Select(id => (PyValue)new PyLong(id))]),
            request.MailingListId is { } listId ? new PyLong(listId) : PyNone.Instance,
            organizationId is > 0 ? new PyLong(organizationId.Value) : PyNone.Instance,
            new PyStr(request.Title),
            new PyLong(Math.Max(0, statusMask)),
        ];
    }
}

public sealed record MailboxMutationRequest(
    long CharacterId,
    string Method,
    IReadOnlyList<PyValue> Arguments,
    ClientSession Session);

public delegate Task<MailboxMutationResult> MailboxMutationCallback(
    MailboxMutationRequest request,
    CancellationToken cancellationToken);

public sealed record MailboxMutationResult(
    IReadOnlyList<(long Id, long StatusMask, long LabelMask)> Changed,
    IReadOnlyList<long> RemovedStatuses,
    IReadOnlyList<long> PrunedMessages,
    long? CreatedLabelId = null,
    string? CreatedLabelName = null,
    long? CreatedLabelColor = null,
    bool LabelsChanged = false)
{
    public static MailboxMutationResult Empty { get; } = new([], [], []);
}

public static class MailboxMutation
{
    private const long MailStatusRead = 1;
    private const long MailStatusTrashed = 8;
    private const long MailSystemLabelMask = 15;
    private const long FirstCustomLabelMask = 16;
    private const int MaxCustomLabels = 25;
    private const int MaxLabelSize = 40;

    public static MailboxMutationResult Apply(JsonObject state, MailboxMutationRequest request)
    {
        var labelOperation = request.Method is "CreateLabel" or "EditLabel" or "DeleteLabel";
        var mailboxes = state["mailboxes"] as JsonObject;
        var mailboxKey = request.CharacterId.ToString(CultureInfo.InvariantCulture);
        var mailbox = mailboxes?[mailboxKey] as JsonObject;
        if (mailbox is null && labelOperation)
        {
            if (mailboxes is null)
                state["mailboxes"] = mailboxes = new JsonObject();
            mailbox = new JsonObject { ["statuses"] = new JsonObject(), ["labels"] = new JsonObject() };
            mailboxes[mailboxKey] = mailbox;
        }
        var messages = state["messages"] as JsonObject;
        if (mailbox is null || (!labelOperation && messages is null))
            return MailboxMutationResult.Empty;
        if (mailbox["statuses"] is not JsonObject statuses)
        {
            if (!labelOperation)
                return MailboxMutationResult.Empty;
            mailbox["statuses"] = statuses = new JsonObject();
        }
        messages ??= new JsonObject();

        var arguments = request.Arguments;
        var requestedIds = arguments.Count > 0 ? Ids(arguments[0]).ToHashSet() : [];
        var selector = arguments.Count > 0 ? Integer(arguments[0]) : 0;
        var labelMask = arguments.Count > 1 ? Integer(arguments[1]) : 0;

        if (request.Method == "CreateLabel")
            return CreateLabel(mailbox, arguments);
        if (request.Method == "EditLabel")
            return EditLabel(mailbox, arguments);
        if (request.Method == "DeleteLabel")
            return DeleteLabel(mailbox, statuses, arguments);

        var changed = new List<(long Id, long StatusMask, long LabelMask)>();
        var removed = new List<long>();
        foreach (var (key, node) in statuses.ToArray())
        {
            if (node is not JsonObject status ||
                !long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ||
                id <= 0)
                continue;

            var statusMask = Integer(status["statusMask"]);
            if (request.Method == "DeleteMail" && requestedIds.Contains(id)
                || request.Method == "EmptyTrash" && (statusMask & MailStatusTrashed) != 0)
            {
                statuses.Remove(key);
                removed.Add(id);
                continue;
            }
            if (messages[key] is not JsonObject message)
                continue;

            var currentLabels = Integer(status["labelMask"]);
            var selected = request.Method switch
            {
                "MarkAsRead" or "MarkAsUnread" or "MoveFromTrash" or "MoveToTrash" =>
                    requestedIds.Contains(id),
                "MarkAllAsRead" or "MarkAllAsUnread" or "MoveAllToTrash" or "MoveAllFromTrash" => true,
                "MarkAsReadByLabel" or "MarkAsUnreadByLabel" or "MoveToTrashByLabel" =>
                    (currentLabels & selector) == selector,
                "MarkAsReadByList" or "MarkAsUnreadByList" or "MoveToTrashByList" =>
                    Integer(message["toListID"]) == selector,
                "AssignLabels" or "RemoveLabels" or "ReplaceLabels" => requestedIds.Contains(id),
                _ => false,
            };
            if (!selected)
                continue;
            if (request.Method is "MarkAllAsRead" or "MarkAllAsUnread" or
                "MarkAsReadByLabel" or "MarkAsUnreadByLabel" or
                "MarkAsReadByList" or "MarkAsUnreadByList")
            {
                if ((statusMask & MailStatusTrashed) != 0)
                    continue;
            }

            var nextStatus = request.Method switch
            {
                "MarkAsRead" or "MarkAllAsRead" or "MarkAsReadByLabel" or "MarkAsReadByList" =>
                    statusMask | MailStatusRead,
                "MarkAsUnread" or "MarkAllAsUnread" or "MarkAsUnreadByLabel" or "MarkAsUnreadByList" =>
                    statusMask & ~MailStatusRead,
                "MoveToTrash" or "MoveAllToTrash" or "MoveToTrashByLabel" or "MoveToTrashByList" =>
                    statusMask | MailStatusTrashed,
                "MoveFromTrash" or "MoveAllFromTrash" => statusMask & ~MailStatusTrashed,
                _ => statusMask,
            };
            var nextLabels = request.Method switch
            {
                "AssignLabels" => currentLabels | labelMask,
                "RemoveLabels" => currentLabels & ~labelMask,
                "ReplaceLabels" => (currentLabels & MailSystemLabelMask) | labelMask,
                _ => currentLabels,
            };
            if (nextStatus == statusMask && nextLabels == currentLabels)
                continue;
            status["statusMask"] = nextStatus;
            status["labelMask"] = nextLabels;
            changed.Add((id, nextStatus, nextLabels));
        }

        var pruned = new List<long>();
        foreach (var id in removed)
        {
            var key = id.ToString(CultureInfo.InvariantCulture);
            var stillReferenced = (state["mailboxes"] as JsonObject)?.Any(pair =>
                pair.Value is JsonObject other
                && other["statuses"] is JsonObject otherStatuses
                && otherStatuses.ContainsKey(key)) == true;
            if (!stillReferenced && messages.Remove(key))
                pruned.Add(id);
        }
        return new MailboxMutationResult(changed, removed, pruned);
    }

    private static MailboxMutationResult CreateLabel(JsonObject mailbox, IReadOnlyList<PyValue> arguments)
    {
        var name = NormalizeLabelName(arguments.Count > 0 ? StringValue(arguments[0]) : "");
        if (name.Length == 0)
            return MailboxMutationResult.Empty;
        var labels = mailbox["labels"] as JsonObject;
        if (labels is null)
            mailbox["labels"] = labels = new JsonObject();
        var ownLabelCount = labels.Select(pair => ParseId(pair.Key))
            .Count(id => id > 0 && (id & MailSystemLabelMask) == 0);
        if (ownLabelCount >= MaxCustomLabels)
            return MailboxMutationResult.Empty;

        var meta = mailbox["_meta"] as JsonObject;
        if (meta is null)
            mailbox["_meta"] = meta = new JsonObject();
        var candidate = Math.Max(FirstCustomLabelMask, meta["nextLabelMask"] is null
            ? FirstCustomLabelMask
            : Math.Max(0, Integer(meta["nextLabelMask"])));
        while (labels.ContainsKey(candidate.ToString(CultureInfo.InvariantCulture)))
        {
            if (candidate > long.MaxValue / 2)
                return MailboxMutationResult.Empty;
            candidate *= 2;
        }
        if (candidate > long.MaxValue / 2)
            return MailboxMutationResult.Empty;
        meta["nextLabelMask"] = candidate * 2;
        var color = Math.Max(0, arguments.Count > 1 ? Integer(arguments[1]) : 0);
        labels[candidate.ToString(CultureInfo.InvariantCulture)] = new JsonObject
        {
            ["labelID"] = candidate,
            ["name"] = name,
            ["color"] = color,
        };
        return new MailboxMutationResult([], [], [], candidate, name, color, LabelsChanged: true);
    }

    private static MailboxMutationResult EditLabel(JsonObject mailbox, IReadOnlyList<PyValue> arguments)
    {
        var id = arguments.Count > 0 ? Integer(arguments[0]) : 0;
        if (id <= 0)
            return MailboxMutationResult.Empty;
        var labels = mailbox["labels"] as JsonObject;
        var key = id.ToString(CultureInfo.InvariantCulture);
        var label = labels?[key] as JsonObject;
        var hasName = arguments.Count > 1 && arguments[1] is not PyNone;
        var hasColor = arguments.Count > 2 && arguments[2] is not PyNone;
        if (label is null)
        {
            if (id is not (1 or 2 or 4 or 8) || !hasColor)
                return MailboxMutationResult.Empty;
            if (labels is null)
                mailbox["labels"] = labels = new JsonObject();
            label = new JsonObject { ["labelID"] = id, ["name"] = "", ["color"] = 0 };
            labels[key] = label;
        }
        if (hasName)
        {
            var name = NormalizeLabelName(StringValue(arguments[1]));
            if (name.Length > 0)
                label["name"] = name;
        }
        if (hasColor)
            label["color"] = Math.Max(0, Integer(arguments[2]));
        return new MailboxMutationResult([], [], [], LabelsChanged: true);
    }

    private static MailboxMutationResult DeleteLabel(
        JsonObject mailbox,
        JsonObject statuses,
        IReadOnlyList<PyValue> arguments)
    {
        var id = arguments.Count > 0 ? Integer(arguments[0]) : 0;
        if (id < FirstCustomLabelMask || (id & MailSystemLabelMask) != 0)
            return MailboxMutationResult.Empty;
        var labels = mailbox["labels"] as JsonObject;
        var key = id.ToString(CultureInfo.InvariantCulture);
        if (labels?.Remove(key) != true)
            return MailboxMutationResult.Empty;

        var changed = new List<(long Id, long StatusMask, long LabelMask)>();
        foreach (var (statusKey, node) in statuses)
        {
            if (node is not JsonObject status ||
                !long.TryParse(statusKey, NumberStyles.None, CultureInfo.InvariantCulture, out var messageId)
                || messageId <= 0)
                continue;
            var statusMask = Integer(status["statusMask"]);
            var previousLabels = Integer(status["labelMask"]);
            var nextLabels = previousLabels & ~id;
            if (nextLabels == previousLabels)
                continue;
            status["labelMask"] = nextLabels;
            changed.Add((messageId, statusMask, nextLabels));
        }
        return new MailboxMutationResult(changed, [], [], LabelsChanged: true);
    }

    private static string NormalizeLabelName(string value)
    {
        var name = value.Trim();
        return name.Length > MaxLabelSize ? name[..MaxLabelSize] : name;
    }

    private static string StringValue(PyValue value) => value switch
    {
        PyStr text => text.Value,
        PyBytes bytes => Encoding.UTF8.GetString(bytes.Value.Span),
        _ => "",
    };

    private static IReadOnlyList<long> Ids(PyValue value) => value switch
    {
        PyList list => list.Items.Select(Integer).Where(id => id > 0).Distinct().ToArray(),
        PyTuple tuple => tuple.Items.Select(Integer).Where(id => id > 0).Distinct().ToArray(),
        _ => [],
    };

    private static long ParseId(string value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;

    private static long Integer(PyValue value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        PyReal real when double.IsFinite(real.Value) && real.Value >= long.MinValue
            && real.Value < long.MaxValue && real.Value == Math.Truncate(real.Value) => (long)real.Value,
        _ => 0,
    };

    private static long Integer(JsonNode? value) =>
        long.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number : 0;
}

public static class MailboxMutationNotifications
{
    public static void Publish(
        MailboxMutationRequest request,
        MailboxMutationResult result,
        ClientSession recipientSession)
    {
        if (result.CreatedLabelId is { } labelId)
        {
            recipientSession.SendNotification(
                "OnLabelsCreatedByExternal",
                "clientID",
                Py.KeyVal(
                    ("labelID", new PyInt(labelId)),
                    ("name", new PyStr(result.CreatedLabelName ?? "")),
                    ("color", new PyInt(result.CreatedLabelColor ?? 0))));
            return;
        }
        if (result.RemovedStatuses.Count > 0)
        {
            recipientSession.SendNotification(
                "OnMailDeleted",
                "clientID",
                new PyList([.. result.RemovedStatuses.Select(id => (PyValue)new PyInt(id))]));
            return;
        }
        if (result.Changed.Count == 0 || request.Method is "EditLabel" or "DeleteLabel")
            return;

        var messageIds = new PyList([.. result.Changed.Select(entry => (PyValue)new PyInt(entry.Id))]);
        if (request.Method is "MoveToTrash" or "MoveAllToTrash" or "MoveToTrashByLabel" or "MoveToTrashByList")
        {
            recipientSession.SendNotification("OnMailTrashed", "clientID", messageIds);
        }
        else if (request.Method is "MoveFromTrash" or "MoveAllFromTrash")
        {
            recipientSession.SendNotification("OnMailRestored", "clientID", messageIds);
        }
        else if (request.Method is "AssignLabels" or "RemoveLabels" or "ReplaceLabels")
        {
            foreach (var group in result.Changed.GroupBy(entry => entry.LabelMask))
                recipientSession.SendNotification(
                    "OnMailUpdatedByExternal",
                    "clientID",
                    new PyList([.. group.Select(entry => (PyValue)new PyInt(entry.Id))]),
                    PyNone.Instance,
                    new PyInt(checked((int)group.Key)));
        }
        else
        {
            var isRead = request.Method is
                "MarkAsRead" or "MarkAllAsRead" or "MarkAsReadByLabel" or "MarkAsReadByList";
            recipientSession.SendNotification(
                "OnMailUpdatedByExternal",
                "clientID",
                messageIds,
                PyValue.Of(isRead),
                PyNone.Instance);
        }
    }
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

    public static string BuildBody(string channelName)
    {
        var body = $"A GM would like to speak with you.<br><br>Requested channel: {channelName}".Trim();
        return body.Length > 8000 ? body[..8000] : body;
    }
}

public delegate Task<long?> GmChatPokeMailCallback(
    GmChatPokeMailRequest request,
    ClientSession session,
    CancellationToken cancellationToken);
