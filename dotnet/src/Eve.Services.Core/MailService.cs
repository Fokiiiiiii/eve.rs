using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Core;

/// <summary>
/// The mailMgr read contract from EveJS 828823a75f211fdecb945b087a26c225f111491f:
/// server/src/services/mail/{mailMgrService,mailState}.js.
/// Client: eve-logic 1dbb5711a072a146e1d228365668ac05655eefe1,
/// eve/client/script/ui/services/mail/mailSvc.py (SyncMail, GetBody, GetLabels).
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
    GmChatPokeMailCallback? pokePlayerAboutChatMsgGm = null) : IService
{
    private readonly Func<CancellationToken, Task<JsonObject>> _readMail =
        readMail ?? throw new ArgumentNullException(nameof(readMail));
    private readonly Func<long, long, ClientSession, CancellationToken, Task> _markAsRead =
        markAsRead ?? throw new ArgumentNullException(nameof(markAsRead));
    private readonly Func<MailSendRequest, ClientSession, CancellationToken, Task<long?>>? _sendMail = sendMail;
    private readonly GmChatPokeMailCallback? _pokePlayerAboutChatMsgGm = pokePlayerAboutChatMsgGm;

    public string Name => "mailMgr";

    public bool HasMethod(string method) => method is "SyncMail" or "GetMailHeaders" or "GetBody" or "GetLabels" or "MarkAsRead" or "SendMail" or "PokePlayerAboutChatMsgGm";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method))
            return PyNone.Instance;
        call.Cancellation.ThrowIfCancellationRequested();

        if (call.Method == "MarkAsRead")
        {
            var requestingCharacterId = call.Session.CharacterId.GetValueOrDefault();
            if (requestingCharacterId > 0)
                foreach (var id in Items(call.Arg(0)).Select(PositiveId).Where(id => id > 0).Distinct())
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
                var headers = new List<PyValue>();
                foreach (var id in Items(call.Arg(0)).Select(PositiveId).Where(id => id > 0).Distinct())
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

private async ValueTask<PyValue> SendMailAsync(ServiceCall call)
    {
        var characterId = call.Session.CharacterId.GetValueOrDefault();
        if (characterId <= 0)
            return PyNone.Instance;
        if (_sendMail is null)
            throw new InvalidOperationException("mailMgr.SendMail requires a durable persistence callback.");

        var request = new MailSendRequest(
            characterId,
            RecipientIds(call.Arg(0)),
            OptionalPositiveId(call.Arg(1)),
            OptionalRecipientIds(call.Arg(2)),
            StringArgument(call.Arg(3)),
            StringArgument(call.Arg(4)),
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
            : JsSemantics.StringValue(call.Arg(1)) ?? "Unknown Channel";
        var request = new GmChatPokeMailRequest(
            characterId,
            140000004,
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
        return [.. values.Select(PositiveId).Where(id => id > 0).Distinct()];
    }

private static IReadOnlyList<long>? OptionalRecipientIds(PyValue value) =>
        value is PyNone ? null : RecipientIds(value);

    private static long? OptionalPositiveId(PyValue value) =>
        PositiveId(value) is > 0 and var id ? id : null;

    private static string StringArgument(PyValue value) =>
        value is PyStr text ? text.Value : "";

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

private static PyValue OptionalIds(JsonNode? value) => value is JsonArray ids
        ? new PyList([.. ids.Select(Integer).Where(id => id > 0).Select(id => (PyValue)new PyInt(id))])
        : OptionalId(value);

    private static IReadOnlyList<PyValue> Items(PyValue value) => value switch
    {
        PyList list => list.Items,
        PyTuple tuple => tuple.Items,
        _ => [],
    };

    private static string Key(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static long PositiveId(PyValue value)
    {
        var number = JsSemantics.ToNumber(value);
        return double.IsFinite(number) && number >= 1 && number < long.MaxValue
            ? (long)Math.Truncate(number) : 0;
    }

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

public sealed record MailSendRequest(
    long SenderCharacterId,
    IReadOnlyList<long> CharacterRecipientIds,
    long? MailingListId,
    IReadOnlyList<long>? CorporationOrAllianceRecipientIds,
    string Title,
    string Body,
    long? ReplyToMessageId,
    long? ForwardedFromMessageId);

public sealed record GmChatPokeMailRequest(
    long CharacterId,
    long SenderCharacterId,
    string ChannelName,
    string Title,
    string Body,
    bool SaveSenderCopy);

public delegate Task<long?> GmChatPokeMailCallback(
    GmChatPokeMailRequest request,
    ClientSession session,
    CancellationToken cancellationToken);
