using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.Marshal;
using Eve.Macho;

namespace Eve.Services.Core;

/// <summary>
/// The reader supplies one character's current
/// notifications/boxes/{characterId} document from GameStore (byID plus order).
/// Return null only for a missing box; storage failures must propagate. The service
/// reads a fresh snapshot per call and never changes processed flags or storage.
/// </summary>
/// <remarks>
/// EVE.js 828823a75f211fdecb945b087a26c225f111491f:
/// server/src/services/notifications/{notificationMgrService,notificationState}.js.
/// Client 1dbb5711a072a146e1d228365668ac05655eefe1:
/// eve/client/script/ui/services/mail/notificationSvc.py, notifications/common/notification.py.
/// Responses are lists of util.KeyVal, as consumed by Notification.FromDTO, not rowsets.
/// Register explicitly after providing the persisted reader and the Host clock.
/// </remarks>
public sealed class NotificationMgrService(
    Func<long, CancellationToken, ValueTask<JsonObject?>> readBox,
    Func<long> nowUnixMs,
    NotificationInteractionCallback? logInteraction = null,
    NotificationMutationCallback? mutate = null) : IService
{
    public string Name => "notificationMgr";

    public bool HasMethod(string method) => method is "GetAllNotifications" or "GetByGroupID" or "GetUnprocessed" or "LogNotificationInteraction"
        || mutate is not null && method is "MarkAllAsProcessed" or "MarkAsProcessed" or "DeleteNotifications";

    public ValueTask<PyValue> CallAsync(ServiceCall call) => call.Method switch
    {
        "GetAllNotifications" => ReadAsync(call, fromId: Argument(call, "fromID")),
        "GetByGroupID" => ReadAsync(call, groupId: Argument(call, "groupID")),
        "GetUnprocessed" => ReadAsync(call, unprocessed: true),
        "LogNotificationInteraction" => LogInteractionAsync(call),
        "MarkAllAsProcessed" or "MarkAsProcessed" or "DeleteNotifications" when mutate is not null => MutateAsync(call),
        _ => ValueTask.FromResult<PyValue>(PyNone.Instance),
    };

    private async ValueTask<PyValue> MutateAsync(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var character = Positive(call.Session.Get("charid"));
        if (character <= 0) return PyNone.Instance;
        var ids = call.Method is "MarkAsProcessed" or "DeleteNotifications"
            ? Ids(call.Arg(0))
            : [];
        await mutate!(character, call.Method, null, ids, call.Session, call.Cancellation);
        return PyNone.Instance;
    }

    private static IReadOnlyList<long> Ids(PyValue? value) => value is PyList list
        ? [.. list.Items.Select(Positive).Where(id => id > 0).Distinct()]
        : [];

    private async ValueTask<PyValue> LogInteractionAsync(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var referenceId = Positive(call.Kwarg("referenceID") ?? call.Arg(0));
        if (logInteraction is { } callback)
            await callback(referenceId, call.Session, call.Cancellation);
        return PyNone.Instance;
    }

    private async ValueTask<PyValue> ReadAsync(ServiceCall call, long fromId = 0, long groupId = 0, bool unprocessed = false)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var character = Positive(call.Session.Get("charid"));
        if (character == 0) return new PyList([]);
        var box = await readBox(character, call.Cancellation);
        call.Cancellation.ThrowIfCancellationRequested();
        if (box?["byID"] is not JsonObject records) return new PyList([]);

        // EVE.js rebuilds order from byID, de-duplicates, then sorts by descending ID.
        // Reconstruct that view without mutating the persisted mailbox on a read.
        var selected = new Dictionary<long, JsonObject>();
        foreach (var (key, node) in records)
        {
            if (node is not JsonObject record) continue;
            var savedId = Payload(record["notificationID"]);
            var savedReceiver = Payload(record["receiverID"]);
            var id = Positive(JsSemantics.Truthy(savedId) ? savedId : new PyStr(key));
            var receiver = JsSemantics.Truthy(savedReceiver) ? Positive(savedReceiver) : character;
            if (id <= fromId || receiver != character) continue;
            if (groupId > 0 && Positive(Payload(record["groupID"])) != groupId) continue;
            if (unprocessed && True(record["processed"])) continue;
            selected[id] = record;
        }
        return new PyList([.. selected.OrderByDescending(p => p.Key).Select(p => Dto(p.Key, character, p.Value))]);
    }

    private PyValue Dto(long id, long character, JsonObject record) => Py.KeyVal(
        ("notificationID", new PyInt(id)),
        ("typeID", new PyInt(Positive(Payload(record["typeID"])))),
        ("senderID", new PyInt(Integer(Payload(record["senderID"])))),
        ("receiverID", new PyInt(character)),
        ("processed", PyValue.Of(True(record["processed"]))),
        ("created", FileTime(record["created"])),
        ("data", Data(record)));

    private PyValue Data(JsonObject record)
    {
        var data = record["data"];
        var type = Positive(Payload(record["typeID"]));
        if (type == 1004) return MailData(data as JsonObject);
        if (type == 3001 && data is JsonArray rows)
            return new PyList([.. rows.Select(row => new PyList(row is JsonArray values
                ? [.. values.Select((value, index) => index < 2
                    ? (PyValue)new PyInt(Integer(Payload(value)))
                    : new PyReal(FiniteNumber(Payload(value))))]
                : []))]);
        return NormalizedData(data);
    }

    // notificationState normalizes null array entries, but preserves null fields inside objects.
    private static PyValue NormalizedData(JsonNode? data) => data switch
    {
        null => Py.Dict(),
        JsonArray array => new PyList([.. array.Select(NormalizedData)]),
        _ => Payload(data),
    };

    private PyValue MailData(JsonObject? data)
    {
        var msg = data?["msg"] as JsonObject;
        var senderName = Text(data?["senderName"]);
        var subject = Text(data?["subject"]);
        var recipients = msg?["toCharacterIDs"] is JsonArray ids
            ? ids.Select(id => Positive(Payload(id))).Where(id => id > 0)
            : [];
        return Py.Dict(
            ("senderName", new PyStr(senderName)),
            ("subject", new PyStr(subject)),
            ("msg", Py.KeyVal(
                ("messageID", new PyInt(Positive(Payload(msg?["messageID"])))),
                ("senderID", new PyInt(Integer(Payload(msg?["senderID"])))),
                ("senderName", new PyStr(Text(msg?["senderName"], senderName))),
                ("sentDate", FileTime(msg?["sentDate"])),
                ("toCharacterIDs", new PyList([.. recipients.Select(id => (PyValue)new PyInt(id))])),
                ("toListID", OptionalId(msg?["toListID"])),
                ("toCorpOrAllianceID", OptionalId(msg?["toCorpOrAllianceID"])),
                ("subject", new PyStr(Text(msg?["subject"], subject))),
                ("statusMask", new PyInt(Positive(Payload(msg?["statusMask"])))),
                ("labelMask", new PyInt(Positive(Payload(msg?["labelMask"])))),
                ("read", PyValue.Of(True(msg?["read"]))),
                ("trashed", PyValue.Of(True(msg?["trashed"]))),
                ("replied", PyValue.Of(True(msg?["replied"]))),
                ("forwarded", PyValue.Of(True(msg?["forwarded"])))))
        );
    }

    private PyValue FileTime(JsonNode? value)
    {
        // Do not pass FILETIME through double: the client must receive the exact 64-bit tick count.
        var text = value?.ToString();
        return Py.FileTimeLong(long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
            ? ticks : Py.FileTimeFromUnixMs(nowUnixMs()));
    }

    private static PyValue Payload(JsonNode? value) => value switch
    {
        null => PyNone.Instance,
        JsonObject obj => new PyDict([.. obj.Select(p => new KeyValuePair<PyValue, PyValue>(new PyStr(p.Key), Payload(p.Value)))]),
        JsonArray array => new PyList([.. array.Select(Payload)]),
        _ => value.GetValueKind() switch
        {
            JsonValueKind.String => new PyStr(value.GetValue<string>()),
            JsonValueKind.True => PyBool.True,
            JsonValueKind.False => PyBool.False,
            JsonValueKind.Number => Py.Number(((JsonValue)value).TryGetValue<double>(out var number)
                ? number : JsNumber.NumberFromString(value.ToJsonString())),
            _ => PyNone.Instance,
        },
    };

    private static long Argument(ServiceCall call, string name) =>
        Positive(call.Kwarg(name)) is > 0 and var keyword ? keyword : Positive(call.Arg(0));

    private static double FiniteNumber(PyValue? value)
    {
        var number = JsNumber.ValueToNumber(value ?? PyNone.Instance);
        return double.IsFinite(number) ? number : 0;
    }

    private static long Integer(PyValue? value)
    {
        var number = Math.Truncate(FiniteNumber(value));
        return number >= long.MinValue && number < 9223372036854775808d ? (long)number : 0;
    }

    private static long Positive(PyValue? value) => Math.Max(0, Integer(value));

    private static bool True(JsonNode? value) => value is not null && value.GetValueKind() == JsonValueKind.True;

    private static string Text(JsonNode? value, string fallback = "") =>
        value is not null && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : fallback;

    private static PyValue OptionalId(JsonNode? value) =>
        Positive(Payload(value)) is > 0 and var id ? new PyInt(id) : PyNone.Instance;
}

public delegate ValueTask NotificationInteractionCallback(
    long referenceId,
    ClientSession session,
    CancellationToken cancellationToken);


public delegate ValueTask NotificationMutationCallback(
    long characterId,
    string operation,
    long? groupId,
    IReadOnlyList<long> notificationIds,
    ClientSession session,
    CancellationToken cancellationToken);
