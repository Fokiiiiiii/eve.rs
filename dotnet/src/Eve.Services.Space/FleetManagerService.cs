using Eve.Macho;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space;

[Service("fleetMgr")]
public sealed partial class FleetManagerService(
    FleetStore store,
    Func<long, ClientSession?>? sessionOf = null,
    Func<ClientSession, ClientSession, bool>? sameBubble = null)
{
    private const string WatchlistSessionKey = "fleetWatchlist";
    private const string DamageWatchlistSessionKey = "fleetDamageWatchlist";
    private static readonly System.Collections.Generic.HashSet<string> BroadcastNames = new(StringComparer.Ordinal)
    {
        "EnemySpotted", "HealArmor", "HealShield", "HealCapacitor", "InPosition", "NeedBackup",
        "HoldPosition", "Location", "TravelTo", "JumpBeacon", "Target", "RepTarget", "WarpTo",
        "AlignTo", "JumpTo"
    };

    [Method("ForceLeaveFleet")]
    public async ValueTask<PyValue> ForceLeaveFleet(ServiceCall call)
    {
        var context = await ContextAsync(call);
        if (context is null) return PyNone.Instance;
        var (fleetId, before, _) = context.Value;
        var characterId = CharacterId(call.Session);
        if (await store.LeaveAsync(fleetId, characterId, call.Cancellation) is null) return PyNone.Instance;
        ClearSession(call.Session);
        var after = await store.SnapshotAsync(fleetId, call.Cancellation);
        PublishMemberChanges(call.Session, fleetId, before, after);
        return PyNone.Instance;
    }

    [Method("AddToWatchlist")]
    public async ValueTask<PyValue> AddToWatchlist(ServiceCall call)
    {
        var context = await ContextAsync(call);
        if (context is null) return PyNone.Instance;
        var (fleetId, snapshot, _) = context.Value;
        var members = snapshot.Members.Select(member => member.CharacterId).ToHashSet();
        var added = Ids(call.Arg(0));
        var favorites = Ids(call.Arg(1));
        var watchlist = favorites.Concat(added).Where(members.Contains).Distinct().Take(100).ToArray();
        SetWatchlist(call.Session, WatchlistSessionKey, watchlist);
        return PyNone.Instance;
    }

    [Method("RemoveFromWatchlist")]
    public async ValueTask<PyValue> RemoveFromWatchlist(ServiceCall call)
    {
        var context = await ContextAsync(call);
        if (context is null) return PyNone.Instance;
        var (fleetId, snapshot, _) = context.Value;
        var members = snapshot.Members.Select(member => member.CharacterId).ToHashSet();
        var removed = Number(call.Arg(0));
        var remaining = Ids(call.Arg(1)).Where(id => id != removed && members.Contains(id)).Distinct().Take(100).ToArray();
        SetWatchlist(call.Session, WatchlistSessionKey, remaining);
        var damageWatchlist = Ids(call.Session.Get(DamageWatchlistSessionKey) ?? PyNone.Instance)
            .Where(id => id != removed && members.Contains(id)).Distinct().Take(100).ToArray();
        SetWatchlist(call.Session, DamageWatchlistSessionKey, damageWatchlist);
        return PyNone.Instance;
    }

    [Method("RegisterForDamageUpdates")]
    public async ValueTask<PyValue> RegisterForDamageUpdates(ServiceCall call)
    {
        var context = await ContextAsync(call);
        if (context is null) return PyNone.Instance;
        var (fleetId, snapshot, _) = context.Value;
        var members = snapshot.Members.Select(member => member.CharacterId).ToHashSet();
        var watchlist = Ids(call.Arg(0)).Where(members.Contains).Distinct().Take(100).ToArray();
        SetWatchlist(call.Session, DamageWatchlistSessionKey, watchlist);
        return PyNone.Instance;
    }

    [Method("BroadcastToBubble")]
    public async ValueTask<PyValue> BroadcastToBubble(ServiceCall call)
    {
        await BroadcastAsync(call, systemOnly: false);
        return PyNone.Instance;
    }

    [Method("BroadcastToSystem")]
    public async ValueTask<PyValue> BroadcastToSystem(ServiceCall call)
    {
        await BroadcastAsync(call, systemOnly: true);
        return PyNone.Instance;
    }

    private async Task BroadcastAsync(ServiceCall call, bool systemOnly)
    {
        var context = await ContextAsync(call);
        if (context is null) return;
        var (fleetId, snapshot, sender) = context.Value;
        var name = Text(call.Arg(0));
        var scope = Number(call.Arg(1));
        if (!BroadcastNames.Contains(name) || scope is not (1 or 2 or 3)) return;
        var senderSystem = SystemId(call.Session);
        if (senderSystem is null) return;
        var senderIsInSpace = call.Session.GetInt("stationid") is null;
        if (!senderIsInSpace && !systemOnly) return;

        var itemId = Number(call.Arg(2));
        var typeId = Number(call.Arg(3));
        foreach (var member in snapshot.Members)
        {
            if (!ShouldReceive(member, sender, (int)scope.Value)) continue;
            var recipient = member.CharacterId == sender.CharacterId
                ? call.Session
                : sessionOf?.Invoke(member.CharacterId);
            if (recipient?.CharacterId != member.CharacterId || SystemId(recipient) != senderSystem) continue;
            if (systemOnly)
            {
                recipient.SendNotification("OnFleetBroadcast", "fleetid",
                    new PyStr(name), new PyInt((int)scope.Value), new PyLong(sender.CharacterId),
                    new PyLong(senderSystem.Value), WireId(itemId), WireId(typeId));
                continue;
            }

            var sameGrid = sameBubble?.Invoke(call.Session, recipient) ??
                (call.Session.GetInt("ballparkid") is { } bubbleId &&
                 recipient.GetInt("ballparkid") == bubbleId);
            if (sameGrid)
                recipient.SendNotification("OnFleetBroadcast", "fleetid",
                    new PyStr(name), new PyInt((int)scope.Value), new PyLong(sender.CharacterId),
                    new PyLong(senderSystem.Value), WireId(itemId), WireId(typeId));
        }
    }

    private async Task<(long FleetId, FleetSnapshot Snapshot, FleetMemberSnapshot Member)?> ContextAsync(ServiceCall call)
    {
        var characterId = CharacterId(call.Session);
        if (characterId <= 0 ||
            await store.FleetIdForCharacterAsync(characterId, call.Cancellation) is not { } fleetId ||
            await store.SnapshotAsync(fleetId, call.Cancellation) is not { } snapshot)
            return null;
        var member = snapshot.Members.FirstOrDefault(item => item.CharacterId == characterId);
        return member is null ? null : (fleetId, snapshot, member);
    }

    private void PublishMemberChanges(ClientSession issuer, long fleetId, FleetSnapshot before, FleetSnapshot? after)
    {
        var oldMembers = before.Members.ToDictionary(member => member.CharacterId);
        var newMembers = after?.Members.ToDictionary(member => member.CharacterId) ?? [];
        var recipients = new System.Collections.Generic.HashSet<ClientSession> { issuer };
        foreach (var member in before.Members.Concat(after?.Members ?? []))
        {
            if (sessionOf?.Invoke(member.CharacterId) is { } session && session.CharacterId == member.CharacterId)
                recipients.Add(session);
        }
        foreach (var id in oldMembers.Keys.Union(newMembers.Keys))
        {
            oldMembers.TryGetValue(id, out var oldMember);
            newMembers.TryGetValue(id, out var newMember);
            if (oldMember == newMember) continue;
            var oldOptOuts = oldMember is null ? PyNone.Instance : MemberOptOuts(oldMember);
            var newOptOuts = newMember is null ? PyNone.Instance : MemberOptOuts(newMember);
            foreach (var recipient in recipients)
            {
                if (recipient.CharacterId is not { } recipientId ||
                    recipientId != id && !newMembers.ContainsKey(recipientId)) continue;
                recipient.SendNotification("OnFleetMemberChanged", "fleetid",
                    new PyLong(id), new PyLong(fleetId),
                    NullableLong(oldMember?.WingId), NullableLong(oldMember?.SquadId),
                    NullableInt(oldMember?.Role), NullableInt(oldMember?.Job), oldOptOuts,
                    NullableLong(newMember?.WingId), NullableLong(newMember?.SquadId),
                    NullableInt(newMember?.Role), NullableInt(newMember?.Job), newOptOuts,
                    after?.Members.Count == 1 ? PyBool.True : PyBool.False);
            }
        }
        if (after is not null)
            foreach (var member in after.Members)
                if (sessionOf?.Invoke(member.CharacterId) is { } session && session.CharacterId == member.CharacterId)
                    SetSession(session, fleetId, member);
    }

    private static PyValue MemberOptOuts(FleetMemberSnapshot member) => Py.KeyVal([
        ("acceptsFleetWarp", member.AcceptsFleetWarp ? PyBool.True : PyBool.False),
        ("acceptsConduitJumps", member.AcceptsConduitJumps ? PyBool.True : PyBool.False),
        ("acceptsFleetRegroups", member.AcceptsFleetRegroups ? PyBool.True : PyBool.False)
    ]);

    private static bool ShouldReceive(FleetMemberSnapshot recipient, FleetMemberSnapshot sender, int scope)
    {
        if (scope == 3 || recipient.CharacterId == sender.CharacterId) return true;
        if (scope == 2)
            return sender.Role != FleetStore.Leader &&
                   (recipient.Role == FleetStore.Leader ||
                    sender.SquadId > 0 && recipient.Role != FleetStore.Member && recipient.WingId == sender.WingId);
        if (scope == 1)
            return sender.Role == FleetStore.Leader ||
                   sender.SquadId > 0 && recipient.SquadId == sender.SquadId ||
                   sender.Role == FleetStore.WingCommander && recipient.WingId == sender.WingId;
        return false;
    }

    private static IReadOnlyList<long> Ids(PyValue value) => Sequence(value)
        .Select(Number).Where(id => id is > 0).Select(id => id!.Value).ToArray();

    private static IReadOnlyList<PyValue> Sequence(PyValue value) => value switch
    {
        PyList list => list.Items,
        PyTuple tuple => tuple.Items,
        _ => []
    };

    private static void SetWatchlist(ClientSession session, string key, IReadOnlyList<long> ids) =>
        session.Set(key, new PyList(ids.Select(id => (PyValue)new PyLong(id)).ToArray()));

    private static void SetSession(ClientSession session, long fleetId, FleetMemberSnapshot member)
    {
        session.Set("fleetid", new PyLong(fleetId));
        session.Set("fleetrole", new PyInt(member.Role));
        session.Set("wingid", new PyLong(member.WingId));
        session.Set("squadid", new PyLong(member.SquadId));
    }

    private static void ClearSession(ClientSession session)
    {
        session.Set("fleetid", PyNone.Instance);
        session.Set("fleetrole", PyNone.Instance);
        session.Set("wingid", PyNone.Instance);
        session.Set("squadid", PyNone.Instance);
    }

    private static long CharacterId(ClientSession session) => session.CharacterId ?? session.GetInt("charid") ?? 0;

    private static long? SystemId(ClientSession session) =>
        session.GetInt("solarsystemid2") ?? session.GetInt("solarsystemid");

    private static PyValue WireId(long? id) => id is { } value ? new PyLong(value) : PyNone.Instance;

    private static PyValue NullableLong(long? value) => value is { } id ? new PyLong(id) : PyNone.Instance;
    private static PyValue NullableInt(int? value) => value is { } number ? new PyInt(number) : PyNone.Instance;

    private static long? Number(PyValue? value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        PyReal number when double.IsFinite(number.Value) && number.Value >= long.MinValue && number.Value <= long.MaxValue => (long)number.Value,
        PyStr text when long.TryParse(text.Value, out var parsed) => parsed,
        PyWString text when long.TryParse(text.Value, out var parsed) => parsed,
        _ => null
    };

    private static string Text(PyValue value) => value switch
    {
        PyStr text => text.Value,
        PyWString text => text.Value,
        _ => ""
    };
}
