using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Core;

/// <summary>

/// 828823a75f211fdecb945b087a26c225f111491f, services/mail/mailingListsMgrService.js
/// and mailState.js; client: eve-logic, ui/services/mail/mailingListsSvc.py.
/// </summary>
/// <remarks>
/// The reader supplies a snapshot of the existing GameStore mail.mailingLists object,
/// keyed by list ID. The Host provides the atomic mutation callback for manager writes,
/// including authorization, member notifications, and welcome-mail fan-out.
/// </remarks>
public enum MailingListManagerMethod
{
    Create,
    Join,
    Leave,
    Delete,
    KickMembers,
    SetEntitiesAccess,
    ClearEntityAccess,
    SetMembersMuted,
    SetMembersOperator,
    SetMembersClear,
    SetDefaultAccess,
    SaveWelcomeMail,
    SendWelcomeMail,
    ClearWelcomeMail
}

public sealed record MailingListCaller(long SessionId, long ClientId, long? UserId, long? CharacterId);

public sealed record MailingListMutation(
    MailingListManagerMethod Method,
    MailingListCaller Caller,
    IReadOnlyList<PyValue> PositionalArguments);

/// <summary>
/// The host callback must authorize and mutate the latest mail.mailingLists state in one transaction.
/// SendWelcomeMail must fan out through the existing mail.messages and mail.mailboxes stores.
/// </summary>
public delegate ValueTask<PyValue> MailingListMutationCallback(
    MailingListMutation mutation,
    CancellationToken cancellationToken);

public sealed class MailingListService : IService
{
    private readonly Func<CancellationToken, ValueTask<JsonObject?>> readMailingLists;

    private readonly MailingListMutationCallback? mutateMailingList;

    public MailingListService() : this(_ => ValueTask.FromResult<JsonObject?>(null)) { }

    public MailingListService(
        Func<CancellationToken, ValueTask<JsonObject?>> readMailingLists,
        MailingListMutationCallback? mutateMailingList = null)
    {
        ArgumentNullException.ThrowIfNull(readMailingLists);
        this.readMailingLists = readMailingLists;
        this.mutateMailingList = mutateMailingList;
    }

    public string Name => "mailingListsMgr";
    private const int MaxBulkMutationEntries = 1000;

    public bool HasMethod(string method) => method is
        "GetJoinedLists" or "GetInfo" or "GetMembers" or "GetSettings" or "GetWelcomeMail" ||
        ManagerMethod(method) is not null;

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method))
            return PyNone.Instance;

        call.Cancellation.ThrowIfCancellationRequested();
        if (ManagerMethod(call.Method) is { } managerMethod)
        {
            if ((managerMethod is MailingListManagerMethod.KickMembers or
                 MailingListManagerMethod.SetMembersMuted or
                 MailingListManagerMethod.SetMembersOperator or
                 MailingListManagerMethod.SetMembersClear) &&
                call.Arg(1) is PyList memberTargets && memberTargets.Items.Count > MaxBulkMutationEntries)
                throw MutationLimitRefusal();
            if (managerMethod == MailingListManagerMethod.SetEntitiesAccess &&
                call.Arg(1) is PyDict entityAccess && entityAccess.Entries.Count > MaxBulkMutationEntries)
                throw MutationLimitRefusal();

            var callback = mutateMailingList ??
                throw new InvalidOperationException("mailingListsMgr writes require an atomic persistence callback.");
            var caller = new MailingListCaller(
                call.Session.SessionId,
                call.Session.ClientId,
                call.Session.UserId,
                call.Session.CharacterId);
              var arguments = managerMethod == MailingListManagerMethod.Create
                  ? CreateArguments(call)
                  : call.Args;
            return await callback(
                  new MailingListMutation(managerMethod, caller, arguments),
                call.Cancellation);
        }

        var characterId = PositiveId(JsSemantics.ToNumber(call.Session.Get("charid")));
        var listId = PositiveId(JsSemantics.ToNumber(call.Arg(0)));
        if ((call.Method != "GetInfo" && characterId == 0) ||
            (call.Method != "GetJoinedLists" && listId == 0))
            return Empty(call.Method);

        var lists = await readMailingLists(call.Cancellation);
        if (call.Method == "GetJoinedLists")
        {
            var joined = new List<KeyValuePair<PyValue, PyValue>>();
            if (lists is not null)
                foreach (var (key, node) in lists)
                    if (node is JsonObject record && !Deleted(record) && MemberAccess(record, characterId) is not null)
                    {
                        var id = RecordId(record, key);
                        if (id > 0)
                            joined.Add(new(new PyInt(id), Summary(record, id, characterId)));
                    }
            return new PyDict(joined);
        }

        if (lists?[Key(listId)] is not JsonObject list)
            return Empty(call.Method);
        // EVE.js retains deleted lists' names for display on previously received mail.
        if (call.Method == "GetInfo")
            return Summary(list, RecordId(list, Key(listId)), characterId);
        if (Deleted(list) || MemberAccess(list, characterId) is not (2 or 3))
            return Empty(call.Method);

        return call.Method switch
        {
            "GetMembers" => Members(list),
            "GetSettings" => Py.KeyVal(
                ("defaultAccess", new PyInt(Access(Number(list["defaultAccess"], 1)))),
                ("defaultMemberAccess", new PyInt(Role(Number(list["defaultMemberAccess"], 1)))),
                ("cost", new PyInt(Math.Max(0, Integer(Number(list["cost"]))))),
                ("access", AccessMap(list["access"] as JsonObject))),
            "GetWelcomeMail" when list["welcomeMail"] is JsonObject welcome => new PyList([
                Py.KeyVal(("title", new PyStr(BoundedText(welcome["title"], 150))),
                    ("body", new PyStr(BoundedText(welcome["body"], 8000))))]),
            _ => Empty(call.Method),
        };
    }

    private static MachoWrappedException MutationLimitRefusal() => MachoErrors.ThrowUserError(
        "CustomInfo", ("info", new PyStr("MAILING_LIST_BULK_LIMIT_EXCEEDED")));

    private static MailingListManagerMethod? ManagerMethod(string method) => method switch
    {
        "Create" => MailingListManagerMethod.Create,
        "Join" => MailingListManagerMethod.Join,
        "Leave" => MailingListManagerMethod.Leave,
        "Delete" => MailingListManagerMethod.Delete,
        "KickMembers" => MailingListManagerMethod.KickMembers,
        "SetEntitiesAccess" => MailingListManagerMethod.SetEntitiesAccess,
        "ClearEntityAccess" => MailingListManagerMethod.ClearEntityAccess,
        "SetMembersMuted" => MailingListManagerMethod.SetMembersMuted,
        "SetMembersOperator" => MailingListManagerMethod.SetMembersOperator,
        "SetMembersClear" => MailingListManagerMethod.SetMembersClear,
        "SetDefaultAccess" => MailingListManagerMethod.SetDefaultAccess,
        "SaveWelcomeMail" => MailingListManagerMethod.SaveWelcomeMail,
        "SendWelcomeMail" => MailingListManagerMethod.SendWelcomeMail,
        "ClearWelcomeMail" => MailingListManagerMethod.ClearWelcomeMail,
        _ => null,
    };

    private static IReadOnlyList<PyValue> CreateArguments(ServiceCall call) =>
    [
        call.Arg(0),
        new PyInt(Access(CreateNumber(call.Arg(1), 1))),
        new PyInt(Role(CreateNumber(call.Arg(2), 1))),
        IntegerWire(Math.Max(0, Math.Truncate(CreateNumber(call.Arg(3), 0)))),
    ];

    private static double CreateNumber(PyValue value, double fallback)
    {
        if (value is PyNone)
            return fallback;
        var number = JsSemantics.ToNumber(value);
        return double.IsFinite(number) ? number : fallback;
    }

    private static PyValue IntegerWire(double value)
    {
        if (value <= int.MaxValue)
            return new PyInt((int)value);
        return value >= long.MaxValue ? new PyLong(long.MaxValue) : new PyLong((long)value);
    }

    private static PyValue Empty(string method) => method switch
    {
        "GetJoinedLists" or "GetMembers" => new PyDict([]),
        "GetWelcomeMail" => new PyList([]),
        _ => PyNone.Instance,
    };

    private static PyValue Summary(JsonObject record, long id, long characterId)
    {
        var rawName = Text(record["displayName"]);
        if (rawName.Length == 0)
            rawName = Text(record["name"]);
        var displayName = Regex.Replace(BoundedText(JsonValue.Create(rawName), 60), @"[\s\uFEFF]+", " ").Trim();
        var name = displayName.Replace(" ", "", StringComparison.Ordinal).Split('\\')[^1].ToLowerInvariant();
        var role = MemberAccess(record, characterId);
        return Py.KeyVal(
            ("id", new PyInt(id)), ("name", new PyStr(name)), ("displayName", new PyStr(displayName)),
            ("isMuted", PyValue.Of(role == 0)), ("isOperator", PyValue.Of(role == 2)),
            ("isOwner", PyValue.Of(role == 3)));
    }

    private static PyValue Members(JsonObject list)
    {
        var members = new Dictionary<long, int>();
        if (list["members"] is JsonObject stored)
            foreach (var (key, value) in stored)
                if (PositiveId(JsNumber.NumberFromString(key)) is var id && id > 0)
                    members[id] = Role(Number(value, 1));
        var ownerId = PositiveId(Number(list["ownerID"]));
        if (ownerId > 0)
            members[ownerId] = 3;
        return new PyDict(members.Select(pair => new KeyValuePair<PyValue, PyValue>(new PyInt(pair.Key), new PyInt(pair.Value))).ToArray());
    }

    private static PyValue AccessMap(JsonObject? access)
    {
        var entries = new Dictionary<long, int>();
        if (access is not null)
            foreach (var (key, value) in access)
                if (PositiveId(JsNumber.NumberFromString(key)) is var id && id > 0)
                    entries[id] = Access(Number(value, 1));
        return new PyDict(entries.Select(pair => new KeyValuePair<PyValue, PyValue>(new PyInt(pair.Key), new PyInt(pair.Value))).ToArray());
    }

    private static int? MemberAccess(JsonObject list, long characterId)
    {
        if (characterId <= 0)
            return null;
        if (PositiveId(Number(list["ownerID"])) == characterId)
            return 3;
        return list["members"] is JsonObject members && members.TryGetPropertyValue(Key(characterId), out var role)
            ? Role(Number(role, 1)) : null;
    }

    private static bool Deleted(JsonObject list) => list["deleted"] is JsonValue value && value.TryGetValue<bool>(out var deleted) && deleted;
    private static string Key(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static long RecordId(JsonObject list, string key) => PositiveId(Number(list["listID"])) is var id && id > 0 ? id : PositiveId(JsNumber.NumberFromString(key));
    private static long PositiveId(double value) => value > 0 && value <= 9_007_199_254_740_991d ? (long)Math.Truncate(value) : 0;
    private static long Integer(double value) => double.IsFinite(value) && value >= long.MinValue && value < (double)long.MaxValue ? (long)Math.Truncate(value) : 0;
    private static int Access(double value) => Math.Truncate(value) == 0 ? 0 : 1;
    private static int Role(double value) => Math.Truncate(value) switch { 0 => 0, 2 => 2, 3 => 3, _ => 1 };
    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    private static string BoundedText(JsonNode? node, int length)
    {
        var text = JsNumber.TrimJs(Text(node));
        return text.Length > length ? text[..length] : text;
    }

    private static double Number(JsonNode? node, double fallback = 0)
    {
        if (node is not JsonValue value)
            return fallback;
        double result;
        if (value.TryGetValue<double>(out var real)) result = real;
        else if (value.TryGetValue<long>(out var integer)) result = integer;
        else if (value.TryGetValue<int>(out var small)) result = small;
        else if (value.TryGetValue<string>(out var text)) result = JsNumber.TrimJs(text).Length == 0 ? fallback : JsNumber.NumberFromString(text);
        else if (value.TryGetValue<bool>(out var flag)) result = flag ? 1 : 0;
        else return fallback;
        return double.IsFinite(result) ? result : fallback;
    }
}
