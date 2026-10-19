using Eve.AccessGroups;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

/// <summary>

/// <see cref="AccessRules"/> and the state is <see cref="AccessGroupStore"/>. A change is heard after its response, by the sessions whose

/// </summary>
[Service("ownerGroupManager")]
public sealed partial class OwnerGroupManagerService(AccessGroupStore store, Func<long, ValueTask<MemberKind?>> kindOf, CharacterSessions? sessions,
    SharedFolderNotifier? folders = null)
{
    [Method("GetMyGroups")]
    public async ValueTask<PyValue> GetMyGroups(ServiceCall call)
    {
        // a reader with no acting character has no groups (the client probes with one)
        if ((call.Session.CharacterId ?? 0) <= 0) return new PyList([]);
        return new PyList([.. (await store.VisibleGroupsAsync(Who(call), call.Cancellation)).Select(GroupPayload)]);
    }

    [Method("GetGroup")]
    public async ValueTask<PyValue> GetGroup(ServiceCall call) =>
        await store.VisibleGroupAsync(Who(call), Integral(call.Arg(0)) ?? 0, call.Cancellation) is { } view ? GroupPayload(view) : PyNone.Instance;

    [Method("GetGroupsMany")]
    public async ValueTask<PyValue> GetGroupsMany(ServiceCall call)
    {
        var who = Who(call);
        var groups = new List<PyValue>();
        foreach (var groupId in IdsOf(call.Arg(0)))
            if (await store.VisibleGroupAsync(who, groupId, call.Cancellation) is { } view) groups.Add(GroupPayload(view));
        return new PyList(groups);
    }

    [Method("GetMembers")]
    public async ValueTask<PyValue> GetMembers(ServiceCall call) =>
        new PyList([.. (await store.MembersAsync(Who(call), Integral(call.Arg(0)) ?? 0, call.Cancellation)).Select(MemberPayload)]);

    /// <summary>The members of every group the character can see, keyed by group id.</summary>
    [Method("GetMembersForMultipleGroups")]
    public async ValueTask<PyValue> GetMembersForMultipleGroups(ServiceCall call)
    {
        var who = Who(call);
        var entries = new List<KeyValuePair<PyValue, PyValue>>();
        foreach (var groupId in IdsOf(call.Arg(0)))
        {
            if (await store.VisibleGroupAsync(who, groupId, call.Cancellation) is null) continue;
            var members = await store.MembersAsync(who, groupId, call.Cancellation);
            entries.Add(new(InventoryWire.Id(groupId), new PyList([.. members.Select(MemberPayload)])));
        }
        return new PyDict(entries);
    }

    [Method("Create")]
    public async ValueTask<PyValue> Create(ServiceCall call)
    {
        var who = Who(call);
        var mutation = await store.CreateAsync(who, Text(call.Arg(0)), Text(call.Arg(1)), call.Cancellation);
        Broadcast(call, mutation.Changes);
        return GroupPayload(new GroupView(mutation.Result, MembershipType.Admin, [who.CharacterId]));
    }

    [Method("Delete")]
    public async ValueTask<PyValue> Delete(ServiceCall call)
    {
        var groupId = Integral(call.Arg(0)) ?? 0;
        Broadcast(call, (await store.DeleteAsync(Who(call), groupId, call.Cancellation)).Changes);
        await AccessChanged(call, groupId);
        return PyNone.Instance;
    }

    [Method("UpdateName")]
    public async ValueTask<PyValue> UpdateName(ServiceCall call)
    {
        Broadcast(call, (await store.RenameAsync(Who(call), Integral(call.Arg(0)) ?? 0, Text(call.Arg(1)), call.Cancellation)).Changes);
        return PyNone.Instance;
    }

    [Method("UpdateDescription")]
    public async ValueTask<PyValue> UpdateDescription(ServiceCall call)
    {
        Broadcast(call, (await store.DescribeAsync(Who(call), Integral(call.Arg(0)) ?? 0, Text(call.Arg(1)), call.Cancellation)).Changes);
        return PyNone.Instance;
    }

    /// <summary>Adds the entries of a dict (member id to membership type). Answers the refused ones as (member id, reason) pairs.</summary>
    [Method("AddMembers")]
    public async ValueTask<PyValue> AddMembers(ServiceCall call)
    {
        var who = Who(call);
        var groupId = Integral(call.Arg(0)) ?? 0;
        var entries = new List<NewMember>();
        var failures = new List<MemberFailure>();
        foreach (var (memberId, typeValue) in EntriesOf(call.Arg(1)))
        {
            if (await kindOf(memberId) is not { } kind) { failures.Add(new(memberId, "InvalidMember")); continue; }
            if (!TryType(typeValue, out var type)) { failures.Add(new(memberId, "NotAllowed")); continue; }
            entries.Add(new NewMember(memberId, kind, type));
        }
        var mutation = await store.AddMembersAsync(who, groupId, entries, call.Cancellation);
        Broadcast(call, mutation.Changes);
        await AccessChanged(call, groupId);
        return Failures(failures.Concat(mutation.Result));
    }

    /// <summary>Removes the listed members. Answers the refused ones as (member id, reason) pairs.</summary>
    [Method("DeleteMembers")]
    public async ValueTask<PyValue> DeleteMembers(ServiceCall call)
    {
        var groupId = Integral(call.Arg(0)) ?? 0;
        var mutation = await store.DeleteMembersAsync(Who(call), groupId, IdsOf(call.Arg(1)), call.Cancellation);
        Broadcast(call, mutation.Changes);
        await AccessChanged(call, groupId);
        return Failures(mutation.Result);
    }

    /// <summary>Sets the membership type of the listed members. Answers the refused ones as (member id, reason) pairs.</summary>
    [Method("UpdateMembershipTypes")]
    public async ValueTask<PyValue> UpdateMembershipTypes(ServiceCall call)
    {
        var who = Who(call);
        var groupId = Integral(call.Arg(0)) ?? 0;
        var memberIds = IdsOf(call.Arg(1));
        if (!TryType(call.Arg(2), out var type))
            return Failures(memberIds.Select(id => new MemberFailure(id, "NotAllowed")));
        var mutation = await store.ChangeMembershipTypesAsync(who, groupId, memberIds, type, call.Cancellation);
        Broadcast(call, mutation.Changes);
        await AccessChanged(call, groupId);
        return Failures(mutation.Result);
    }

    [Method("GetGroupLogs")]
    public async ValueTask<PyValue> GetGroupLogs(ServiceCall call) =>
        new PyList([.. (await store.LogsAsync(Who(call), Integral(call.Arg(0)) ?? 0, call.Cancellation)).Select(LogPayload)]);

    /// <summary>After the response, the shared folders that use the group tell their connected characters what the change did to them.</summary>
    private async Task AccessChanged(ServiceCall call, long groupId)
    {
        if (folders is not null && await folders.PlanAccessChangesAsync(groupId, call.Cancellation) is { } send)
            call.Session.AfterResponse(send);
    }

    private static Affiliation Who(ServiceCall call)
    {
        var id = call.Session.CharacterId ?? 0;
        if (id <= 0) throw Refuse("Access groups need a character.");
        return new Affiliation(id, call.Session.GetInt("corpid") ?? 0, call.Session.GetInt("allianceid") ?? 0);
    }

    /// <summary>Sends the changes to every session that one of them names, after the response.</summary>
    private void Broadcast(ServiceCall call, IReadOnlyList<GroupChange> changes)
    {
        if (sessions is null || changes.Count == 0) return;
        call.Session.AfterResponse(() =>
        {
            foreach (var session in sessions.All())
            {
                var heard = changes.Where(change => Hears(change.Audience, session)).ToList();
                if (heard.Count == 0) continue;
                var payload = new PyDict(heard.Select(change => new KeyValuePair<PyValue, PyValue>(InventoryWire.Id(change.GroupId),
                    new PyList([.. change.Codes.Select(code => (PyValue)new PyInt(code))]))).ToArray());
                session.SendNotification("OnOwnerGroupsUpdated", "clientID", payload);
            }
        });
    }

    private static bool Hears(Audience audience, ClientSession session) =>
        audience.Everyone
        || (session.CharacterId is { } character && audience.Characters.Contains(character))
        || (session.GetInt("corpid") is { } corporation && audience.Corporations.Contains(corporation))
        || (session.GetInt("allianceid") is { } alliance && audience.Alliances.Contains(alliance));

    private static bool TryType(PyValue value, out MembershipType type)
    {
        var number = Integral(value);
        type = number is >= 0 and <= 3 ? (MembershipType)(int)number.Value : MembershipType.None;
        return number is >= 0 and <= 3;
    }

    private static PyValue GroupPayload(GroupView view) => Py.KeyVal(
        ("groupID", InventoryWire.Id(view.Group.GroupId)),
        ("creatorID", InventoryWire.Id(view.Group.CreatorId)),
        ("name", new PyWString(view.Group.Name)),
        ("description", new PyWString(view.Group.Description)),
        ("membershipType", new PyInt((int)view.Role)),
        ("admins", new PyList([.. view.Admins.Select(InventoryWire.Id)])));

    private static PyValue MemberPayload(AccessMember member) => Py.KeyVal(
        ("memberID", InventoryWire.Id(member.MemberId)),
        ("membershipType", new PyInt((int)member.Type)));

    private static PyValue LogPayload(AccessLogEntry entry) => Py.KeyVal(
        ("logDate", new PyLong(Py.FileTimeFromUnixMs(entry.LoggedMs))),
        ("actionID", new PyInt(entry.ActionId)),
        ("characterID", InventoryWire.Id(entry.CharacterId)),
        ("ownerID", InventoryWire.Id(entry.OwnerId)),
        ("oldMembershipType", entry.OldType is { } oldType ? new PyInt(oldType) : PyNone.Instance),
        ("newMembershipType", entry.NewType is { } newType ? new PyInt(newType) : PyNone.Instance),
        ("oldText", entry.OldText is null ? PyNone.Instance : new PyWString(entry.OldText)),
        ("newText", entry.NewText is null ? PyNone.Instance : new PyWString(entry.NewText)));

    private static PyValue Failures(IEnumerable<MemberFailure> failures) =>
        new PyList([.. failures.Select(failure => (PyValue)new PyTuple([InventoryWire.Id(failure.MemberId), new PyStr(failure.Reason)]))]);

    private static IEnumerable<(long MemberId, PyValue Type)> EntriesOf(PyValue value) =>
        value is PyDict dict
            ? dict.Entries.Select(entry => (Integral(entry.Key) ?? 0, entry.Value)).Where(entry => entry.Item1 > 0)
            : [];

    /// <summary>A list, tuple or set of ids (the client sends a Python set for bookmark ids and a list for group ids).</summary>
    private static IReadOnlyList<long> IdsOf(PyValue value) =>
        ArgsOf(value).Select(Integral).Where(id => id is > 0).Select(id => id!.Value).Distinct().ToList();

    private static IEnumerable<PyValue> ArgsOf(PyValue value) => value switch
    {
        PyList list => list.Items,
        PyTuple tuple => tuple.Items,
        PyObjectEx { Header: PyTuple { Items.Count: 2 } header } ex when header.Items[0] is PyToken { Value: "__builtin__.set" } && header.Items[1] is PyTuple { Items.Count: 1 } payload
            => (payload.Items[0] is PyList first ? first.Items : []).Concat(ex.List),
        _ => [],
    };

    private static long? Integral(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        PyReal real when double.IsFinite(real.Value) && real.Value == Math.Floor(real.Value) => (long)real.Value,
        _ => null,
    };

    private static string Text(PyValue value) => Eve.Macho.JsSemantics.StringValue(value) ?? "";

    /// <summary>The refusal the client shows: the store's notice as a custom notice (the store's codes are not client error keys).</summary>
    private static MachoWrappedException Refuse(string text) => MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(text)));
}

/// <summary>Answers a refusal of the access group rules as the client's notice.</summary>
public sealed class OwnerGroupRefusals(IService inner) : IService
{
    public string Name => inner.Name;

    public bool HasMethod(string method) => inner.HasMethod(method);

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        try
        {
            return await inner.CallAsync(call);
        }
        catch (AccessGroupError error)
        {
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(error.Notice ?? error.Code)));
        }
    }
}
