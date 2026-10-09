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
    static long? MailingListPyLong(Eve.Marshal.PyValue? value)
    {
        var number = value switch
        {
            Eve.Marshal.PyInt small => (double)small.Value,
            Eve.Marshal.PyLong large => large.Value,
            Eve.Marshal.PyReal real => real.Value,
            _ => double.NaN,
        };
        return double.IsFinite(number) && number >= long.MinValue && number < (double)long.MaxValue
            ? (long)Math.Truncate(number)
            : null;
    }

    static string? MailingListPyText(Eve.Marshal.PyValue? value) => value switch
    {
        Eve.Marshal.PyStr text => text.Value,
        Eve.Marshal.PyWString text => text.Value,
        _ => null,
    };

    static long MailingListJsonLong(JsonNode? value) =>
        long.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0;

    static string MailingListJsonText(JsonNode? value) =>
        value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : string.Empty;

    static string MailingListKey(long id) =>
        id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    static bool MailingListDeleted(JsonObject row) =>
        row["deleted"] is JsonValue deleted && deleted.TryGetValue<bool>(out var value) && value;

    static long MailingListRole(JsonObject row, long characterId)
    {
        if (MailingListJsonLong(row["ownerID"]) == characterId)
            return 3;
        var members = row["members"] as JsonObject;
        return MailingListMemberRole(MailingListJsonLong(members?[MailingListKey(characterId)]));
    }

    static long MailingListMemberRole(long value) => value is 0 or 2 or 3 ? value : 1;

    static long MailingListAccess(long value) => value == 0 ? 0 : 1;

    static bool MailingListCanManageTarget(long actorRole, long targetRole) =>
        actorRole is 2 or 3 && targetRole != 3 && !(actorRole == 2 && targetRole == 2);

    static string MailingListTimestamp(long unixMs) =>
        Py.FileTimeFromUnixMs(unixMs).ToString(System.Globalization.CultureInfo.InvariantCulture);

    static string MailingListBoundedText(string? value, int length)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length > length ? text[..length] : text;
    }

    static bool MailingListCanManage(JsonObject row, long characterId) =>
        MailingListRole(row, characterId) is 2 or 3;

    static (string Name, string DisplayName) NormalizeMailingListName(string raw)
    {
        var display = string.Join(" ", raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
        var name = new string(display.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return (name, display);
    }

    async Task PersistMailingListRowAsync(
        long listId, JsonObject row, CancellationToken cancellationToken)
    {
        var key = MailingListKey(listId);
        var result = mailCache.Write("mail", $"/mailingLists/{key}", row);
        if (!result.Success)
            throw new InvalidOperationException($"Could not persist mailing list {listId}: {result.ErrorMsg}");

        for (var attempt = 0; attempt < 500; attempt++)
        {
            await mailCache.FlushTableAsync("mail", cancellationToken);
            var persisted = await mailStore.LoadTableAsync("mail", createIfMissing: false, cancellationToken);
            if ((persisted["mailingLists"] as JsonObject)?[key] is JsonObject actual &&
                JsonNode.DeepEquals(actual, row))
                return;
            await Task.Delay(10, cancellationToken);
        }
        throw new TimeoutException($"Mailing list {listId} was not durable after its GameStore flush.");
    }

    private Task MailingLists()
    {
        mailingListMutationGate = new SemaphoreSlim(1, 1);

        mutateMailingList = async (mutation, cancellationToken) =>
            {
                var caller = mutation.Caller;
                if (caller.CharacterId is not { } characterId || characterId <= 0)
                    throw new UnauthorizedAccessException("An authenticated character is required for mailing-list mutations.");
                var session = characterSessions.Of(characterId);
                if (session is null || session.SessionId != caller.SessionId ||
                    session.ClientId != caller.ClientId || session.UserId != caller.UserId ||
                    session.CharacterId != characterId)
                    throw new UnauthorizedAccessException("The mailing-list caller does not match the current character session.");

                await mailingListMutationGate.WaitAsync(cancellationToken);
                try
                {
                    var lists = mailCache.Read("mail", "/mailingLists").Data as JsonObject ?? new JsonObject();
                    var args = mutation.PositionalArguments;

                    if (mutation.Method == Eve.Services.Core.MailingListManagerMethod.Create)
                    {
                        if (args.Count < 1 || MailingListPyText(args[0]) is not { } requestedName)
                            return Eve.Marshal.PyNone.Instance;
                        var (name, displayName) = NormalizeMailingListName(requestedName);
                        if (name.Length is 0 or > 64 ||
                            lists.Any(entry => entry.Value is JsonObject existing &&
                                !MailingListDeleted(existing) &&
                                string.Equals(MailingListJsonText(existing["name"]), name, StringComparison.OrdinalIgnoreCase)))
                            return Eve.Marshal.PyNone.Instance;

                        var nextId = Math.Max(500_000_000L, lists
                            .Select(entry => long.TryParse(entry.Key, out var existingId) ? existingId : 0)
                            .DefaultIfEmpty(499_999_999L)
                            .Max() + 1);
                        var defaultAccess = args.Count > 1 ? MailingListPyLong(args[1]) ?? 0 : 0;
                        var defaultMemberAccess = args.Count > 2 ? MailingListPyLong(args[2]) ?? 0 : 0;
                        var cost = args.Count > 3 ? MailingListPyLong(args[3]) ?? 0 : 0;
                        if (cost < 0)
                            return Eve.Marshal.PyNone.Instance;
                        var row = new JsonObject
                        {
                            ["listID"] = nextId,
                            ["name"] = name,
                            ["displayName"] = displayName,
                            ["ownerID"] = characterId,
                            ["members"] = new JsonObject { [MailingListKey(characterId)] = 0 },
                            ["defaultAccess"] = defaultAccess,
                            ["defaultMemberAccess"] = defaultMemberAccess,
                            ["cost"] = cost,
                            ["access"] = new JsonObject(),
                            ["welcomeMail"] = null,
                        };
                        await PersistMailingListRowAsync(nextId, row, cancellationToken);
                        return new Eve.Marshal.PyLong(nextId);
                    }

                    if (mutation.Method == Eve.Services.Core.MailingListManagerMethod.Join)
                    {
                        if (args.Count < 1 || MailingListPyText(args[0]) is not { } requestedName)
                            return Eve.Marshal.PyNone.Instance;
                        var (normalized, _) = NormalizeMailingListName(requestedName);
                        var found = lists.FirstOrDefault(entry => entry.Value is JsonObject row &&
                            !MailingListDeleted(row) &&
                            string.Equals(MailingListJsonText(row["name"]), normalized, StringComparison.OrdinalIgnoreCase));
                        if (found.Value is not JsonObject list)
                            return Eve.Marshal.PyNone.Instance;
                        var id = MailingListJsonLong(list["listID"]);
                        var members = list["members"] as JsonObject ?? new JsonObject();
                        var memberKey = MailingListKey(characterId);
                        var role = MailingListRole(list, characterId);
                        if (members.ContainsKey(memberKey))
                            return MailingListJoinValue(id, list, characterId, role);

                        var defaultAccess = MailingListJsonLong(list["defaultAccess"]);
                        var explicitAccess = list["access"] as JsonObject;
                        var corporationId = session.GetInt("corpid") ?? 0;
                        var allianceId = session.GetInt("allianceid") ?? 0;
                        var entityAccess = corporationId > 0 ? explicitAccess?[MailingListKey(corporationId)] : null;
                        entityAccess ??= allianceId > 0 ? explicitAccess?[MailingListKey(allianceId)] : null;
                        var allowed = defaultAccess == 0 || entityAccess is not null && MailingListJsonLong(entityAccess) == 0;
                        if (!allowed)
                            return Eve.Marshal.PyNone.Instance;

                        var updated = (JsonObject)list.DeepClone();
                        var updatedMembers = updated["members"] as JsonObject ?? new JsonObject();
                        var memberRole = MailingListJsonLong(updated["defaultMemberAccess"]);
                        updatedMembers[memberKey] = memberRole;
                        updated["members"] = updatedMembers;
                        await PersistMailingListRowAsync(id, updated, cancellationToken);
                        return MailingListJoinValue(id, updated, characterId, memberRole);
                    }

                    if (args.Count == 0 || MailingListPyLong(args[0]) is not { } listId || listId <= 0)
                        return Eve.Marshal.PyNone.Instance;
                    var key = MailingListKey(listId);
                    if (lists[key] is not JsonObject current || MailingListDeleted(current))
                        return Eve.Marshal.PyNone.Instance;

                    var currentRole = MailingListRole(current, characterId);
                    var membersNow = current["members"] as JsonObject ?? new JsonObject();
                    switch (mutation.Method)
                    {
                        case Eve.Services.Core.MailingListManagerMethod.Leave:
                        {
                            if (!membersNow.ContainsKey(MailingListKey(characterId)) ||
                                MailingListJsonLong(current["ownerID"]) == characterId)
                                return Eve.Marshal.PyNone.Instance;
                            var updated = (JsonObject)current.DeepClone();
                            (updated["members"] as JsonObject)?.Remove(MailingListKey(characterId));
                            await PersistMailingListRowAsync(listId, updated, cancellationToken);
                            return Eve.Marshal.PyNone.Instance;
                        }
                        case Eve.Services.Core.MailingListManagerMethod.Delete:
                        {
                            if (MailingListJsonLong(current["ownerID"]) != characterId)
                                return Eve.Marshal.PyNone.Instance;
                            var updated = (JsonObject)current.DeepClone();
                            updated["deleted"] = true;
                            updated["deletedAt"] = Py.FileTimeFromUnixMs(now());
                            await PersistMailingListRowAsync(listId, updated, cancellationToken);
                            return Eve.Marshal.PyNone.Instance;
                        }
                        case Eve.Services.Core.MailingListManagerMethod.KickMembers:
                        case Eve.Services.Core.MailingListManagerMethod.SetMembersMuted:
                        case Eve.Services.Core.MailingListManagerMethod.SetMembersOperator:
                        case Eve.Services.Core.MailingListManagerMethod.SetMembersClear:
                        {
                            if (args.Count < 2 || !MailingListCanManage(current, characterId) ||
                                args[1] is not Eve.Marshal.PyList targetList)
                                return Eve.Marshal.PyNone.Instance;
                            var updated = (JsonObject)current.DeepClone();
                            var members = updated["members"] as JsonObject ?? new JsonObject();
                            var defaultRole = MailingListMemberRole(MailingListJsonLong(updated["defaultMemberAccess"]));
                            var seen = new HashSet<long>();
                            var changed = new List<long>();
                            foreach (var targetValue in targetList.Items)
                            {
                                if (MailingListPyLong(targetValue) is not { } targetId || targetId <= 0 ||
                                    !seen.Add(targetId))
                                    continue;
                                if (seen.Count > 1000)
                                    break;
                                var targetKey = MailingListKey(targetId);
                                if (!members.ContainsKey(targetKey))
                                    continue;
                                var targetRole = MailingListRole(current, targetId);
                                if (!MailingListCanManageTarget(currentRole, targetRole))
                                    continue;

                                if (mutation.Method == Eve.Services.Core.MailingListManagerMethod.KickMembers)
                                {
                                    members.Remove(targetKey);
                                    changed.Add(targetId);
                                    continue;
                                }

                                var nextRole = mutation.Method switch
                                {
                                    Eve.Services.Core.MailingListManagerMethod.SetMembersMuted => 0,
                                    Eve.Services.Core.MailingListManagerMethod.SetMembersOperator => 2,
                                    _ => defaultRole,
                                };
                                if (targetRole == nextRole)
                                    continue;
                                members[targetKey] = nextRole;
                                changed.Add(targetId);
                            }
                            if (changed.Count == 0)
                                return Eve.Marshal.PyNone.Instance;

                            updated["members"] = members;
                            updated["updatedAt"] = MailingListTimestamp(now());
                            await PersistMailingListRowAsync(listId, updated, cancellationToken);
                            if (mutation.Method == Eve.Services.Core.MailingListManagerMethod.KickMembers)
                            {
                                foreach (var targetId in changed)
                                    characterSessions.Of(targetId)?.SendNotification(
                                        "OnMailingListLeave", "clientID", new Eve.Marshal.PyLong(listId), new Eve.Marshal.PyLong(targetId));
                            }
                            else
                            {
                                var notification = mutation.Method switch
                                {
                                    Eve.Services.Core.MailingListManagerMethod.SetMembersMuted => "OnMailingListSetMuted",
                                    Eve.Services.Core.MailingListManagerMethod.SetMembersOperator => "OnMailingListSetOperator",
                                    _ => "OnMailingListSetClear",
                                };
                                foreach (var targetId in changed)
                                    characterSessions.Of(targetId)?.SendNotification(
                                        notification, "clientID", new Eve.Marshal.PyLong(listId));
                            }
                            return Eve.Marshal.PyNone.Instance;
                        }
                        case Eve.Services.Core.MailingListManagerMethod.SetEntitiesAccess:
                        {
                            if (args.Count < 2 || !MailingListCanManage(current, characterId) ||
                                args[1] is not Eve.Marshal.PyDict accessByEntity)
                                return Eve.Marshal.PyNone.Instance;
                            var updated = (JsonObject)current.DeepClone();
                            var access = updated["access"] as JsonObject ?? new JsonObject();
                            var updates = 0;
                            foreach (var pair in accessByEntity.Entries)
                            {
                                if (MailingListPyLong(pair.Key) is not { } entityId || entityId <= 0 ||
                                    MailingListPyLong(pair.Value) is not { } mode)
                                    continue;
                                var entityKey = MailingListKey(entityId);
                                var nextAccess = MailingListAccess(mode);
                                if (access.ContainsKey(entityKey) && MailingListJsonLong(access[entityKey]) == nextAccess)
                                    continue;
                                access[entityKey] = nextAccess;
                                if (++updates >= 1000)
                                    break;
                            }
                            if (updates == 0)
                                return Eve.Marshal.PyNone.Instance;
                            updated["access"] = access;
                            updated["updatedAt"] = MailingListTimestamp(now());
                            await PersistMailingListRowAsync(listId, updated, cancellationToken);
                            return Eve.Marshal.PyNone.Instance;
                        }
                        case Eve.Services.Core.MailingListManagerMethod.ClearEntityAccess:
                        {
                            if (args.Count < 2 || !MailingListCanManage(current, characterId) ||
                                MailingListPyLong(args[1]) is not { } entityId || entityId <= 0)
                                return Eve.Marshal.PyNone.Instance;
                            var updated = (JsonObject)current.DeepClone();
                            if ((updated["access"] as JsonObject)?.Remove(MailingListKey(entityId)) != true)
                                return Eve.Marshal.PyNone.Instance;
                            updated["updatedAt"] = MailingListTimestamp(now());
                            await PersistMailingListRowAsync(listId, updated, cancellationToken);
                            return Eve.Marshal.PyNone.Instance;
                        }
                        case Eve.Services.Core.MailingListManagerMethod.SetDefaultAccess:
                        {
                            if (args.Count < 3 || !MailingListCanManage(current, characterId) ||
                                MailingListPyLong(args[1]) is not { } defaultAccess ||
                                MailingListPyLong(args[2]) is not { } defaultMemberAccess)
                                return Eve.Marshal.PyNone.Instance;
                            var updated = (JsonObject)current.DeepClone();
                            updated["defaultAccess"] = MailingListAccess(defaultAccess);
                            updated["defaultMemberAccess"] = MailingListMemberRole(defaultMemberAccess);
                            var cost = args.Count > 3 ? MailingListPyLong(args[3]) ?? 0 : 0;
                            updated["cost"] = Math.Max(0, cost);
                            updated["updatedAt"] = MailingListTimestamp(now());
                            await PersistMailingListRowAsync(listId, updated, cancellationToken);
                            return Eve.Marshal.PyNone.Instance;
                        }
                        case Eve.Services.Core.MailingListManagerMethod.SaveWelcomeMail:
                        case Eve.Services.Core.MailingListManagerMethod.SendWelcomeMail:
                        {
                            if (args.Count < 3 || !MailingListCanManage(current, characterId))
                                return Eve.Marshal.PyNone.Instance;
                            var title = MailingListBoundedText(MailingListPyText(args[1]), 150);
                            var body = MailingListBoundedText(MailingListPyText(args[2]), 8000);
                            var timestamp = MailingListTimestamp(now());
                            var updated = (JsonObject)current.DeepClone();
                            updated["welcomeMail"] = new JsonObject
                            {
                                ["title"] = title,
                                ["body"] = body,
                                ["savedAt"] = timestamp,
                            };
                            updated["updatedAt"] = timestamp;
                            await PersistMailingListRowAsync(listId, updated, cancellationToken);
                            if (mutation.Method == Eve.Services.Core.MailingListManagerMethod.SaveWelcomeMail)
                                return Eve.Marshal.PyNone.Instance;

                            var messageId = await mailSendWriter.SendAsync(
                                new Eve.Services.Core.MailSendRequest(
                                    SenderCharacterId: characterId,
                                    CharacterRecipientIds: [],
                                    MailingListId: listId,
                                    CorporationOrAllianceRecipientIds: null,
                                    Title: title,
                                    Body: body,
                                    ReplyToMessageId: null,
                                    ForwardedFromMessageId: null),
                                session,
                                cancellationToken);
                            return messageId is > 0
                                ? new Eve.Marshal.PyLong(messageId.Value)
                                : Eve.Marshal.PyNone.Instance;
                        }
                        case Eve.Services.Core.MailingListManagerMethod.ClearWelcomeMail:
                        {
                            if (!MailingListCanManage(current, characterId))
                                return Eve.Marshal.PyNone.Instance;
                            var updated = (JsonObject)current.DeepClone();
                            updated["welcomeMail"] = null;
                            updated["updatedAt"] = MailingListTimestamp(now());
                            await PersistMailingListRowAsync(listId, updated, cancellationToken);
                            return Eve.Marshal.PyNone.Instance;
                        }
                        default:
                            return Eve.Marshal.PyNone.Instance;
                    }
                }
                finally
                {
                    mailingListMutationGate.Release();
                }
            };
        return Task.CompletedTask;
    }
}
