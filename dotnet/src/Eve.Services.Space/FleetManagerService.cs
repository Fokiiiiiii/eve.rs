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
    private const int MaxWatchlistInputIds = 500;
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
        if (context is null) throw MachoErrors.ThrowUserError("FleetNotInFleet");
        var (fleetId, snapshot, _) = context.Value;
        var members = snapshot.Members.Select(member => member.CharacterId).ToHashSet();
        var added = Ids(call.Arg(0));
        var favorites = Ids(call.Arg(1));
        var watchlist = favorites.Concat(added).Where(members.Contains).Distinct().Take(100).ToArray();
        SetWatchlist(call.Session, WatchlistSessionKey, watchlist);
        return PyBool.True;
    }

    [Method("RemoveFromWatchlist")]
    public async ValueTask<PyValue> RemoveFromWatchlist(ServiceCall call)
    {
        var context = await ContextAsync(call);
        if (context is null) throw MachoErrors.ThrowUserError("FleetNotInFleet");
        var (fleetId, snapshot, _) = context.Value;
        var members = snapshot.Members.Select(member => member.CharacterId).ToHashSet();
        var removed = WatchlistId(call.Arg(0));
        var remaining = Ids(call.Arg(1)).Where(id => id != removed && members.Contains(id)).Distinct().Take(100).ToArray();
        SetWatchlist(call.Session, WatchlistSessionKey, remaining);
        return PyBool.True;
    }

    [Method("RegisterForDamageUpdates")]
    public async ValueTask<PyValue> RegisterForDamageUpdates(ServiceCall call)
    {
        var context = await ContextAsync(call);
        if (context is null) throw MachoErrors.ThrowUserError("FleetNotInFleet");
        var (fleetId, snapshot, _) = context.Value;
        var members = snapshot.Members.Select(member => member.CharacterId).ToHashSet();
        var watchlist = Ids(call.Arg(0)).Where(members.Contains).Distinct().Take(100).ToArray();
        SetWatchlist(call.Session, WatchlistSessionKey, watchlist);
        return PyBool.True;
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
        ClientSession? LiveSession(long characterId)
        {
            var session = characterId == CharacterId(issuer) ? issuer : sessionOf?.Invoke(characterId);
            return session is not null && CharacterId(session) == characterId ? session : null;
        }

        var recipients = new System.Collections.Generic.HashSet<ClientSession> { issuer };
        foreach (var member in before.Members.Concat(after?.Members ?? []))
            if (LiveSession(member.CharacterId) is { } session)
                recipients.Add(session);

        var changes = oldMembers.Keys.Union(newMembers.Keys).Select(characterId =>
        {
            oldMembers.TryGetValue(characterId, out var oldMember);
            newMembers.TryGetValue(characterId, out var newMember);
            return (CharacterId: characterId, Old: oldMember, New: newMember);
        }).Where(change => change.Old != change.New).ToArray();

        foreach (var change in changes)
        {
            foreach (var recipient in recipients)
            {
                var recipientId = CharacterId(recipient);
                if (recipientId <= 0 || !oldMembers.ContainsKey(recipientId) && !newMembers.ContainsKey(recipientId))
                    continue;
                if (change.New is null)
                    recipient.SendNotification("OnFleetLeave", "fleetid", new PyLong(change.CharacterId));
                else
                    recipient.SendNotification("OnFleetMemberChanged", "fleetid",
                        new PyLong(change.CharacterId), new PyLong(fleetId),
                        NullableLong(change.Old?.WingId), NullableLong(change.Old?.SquadId),
                        NullableInt(change.Old?.Role), NullableInt(change.Old?.Job),
                        change.Old is null ? PyNone.Instance : MemberOptOuts(change.Old),
                        NullableLong(change.New.WingId), NullableLong(change.New.SquadId),
                        NullableInt(change.New.Role), NullableInt(change.New.Job), MemberOptOuts(change.New),
                        after?.Members.Count == 1 ? PyBool.True : PyBool.False);
            }
        }

        foreach (var change in changes)
        {
            if (change.Old is null || change.New is null ||
                change.Old.WingId == change.New.WingId &&
                change.Old.SquadId == change.New.SquadId &&
                change.Old.Role == change.New.Role)
                continue;
            LiveSession(change.CharacterId)?.SendNotification("OnFleetMove", "fleetid");
        }
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

    private static IReadOnlyList<long> Ids(PyValue value)
    {
        var values = Sequence(value);
        var pythonSet = value is PyObjectEx
        {
            Kind: ObjectExKind.Ex1,
            Header: PyTuple { Items.Count: >= 2 } header
        } && header.Items[0] is PyToken { Value: "__builtin__.set" or "builtins.set" } &&
             header.Items[1] is PyList { Items.Count: > 0 } arguments && arguments.Items[0] is PyList;
        if (value is not (PyList or PyTuple) && !pythonSet)
            throw MachoErrors.ThrowUserError("FleetError");
        if (values.Count > MaxWatchlistInputIds)
            throw MachoErrors.ThrowUserError("FleetError");
        return values.Select(WatchlistId).ToArray();
    }

    private static long WatchlistId(PyValue value) => value switch
    {
        PyInt { Value: > 0 } id => id.Value,
        PyLong { Value: > 0 } id => id.Value,
        _ => throw MachoErrors.ThrowUserError("FleetError"),
    };

    private static IReadOnlyList<PyValue> Sequence(PyValue value)
    {
        if (value is PyObjectEx { Kind: ObjectExKind.Ex1, Header: PyTuple header } &&
            header.Items.Count >= 2 &&
            header.Items[0] is PyToken typeName &&
            typeName.Value is "__builtin__.set" or "builtins.set" &&
            header.Items[1] is PyList { Items.Count: > 0 } arguments &&
            arguments.Items[0] is PyList items)
        {
            return items.Items;
        }

        return value switch
        {
            PyList list => list.Items,
            PyTuple tuple => tuple.Items,
            _ => []
        };
    }

    private static void SetWatchlist(ClientSession session, string key, IReadOnlyList<long> ids) =>
        session.Set(key, new PyList(ids.Select(id => (PyValue)new PyLong(id)).ToArray()));

    private static void ClearSession(ClientSession session)
    {
        session.Change(
            ("fleetid", PyNone.Instance),
            ("fleetrole", PyNone.Instance),
            ("wingid", PyNone.Instance),
            ("squadid", PyNone.Instance));
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
        var text when Eve.Macho.JsSemantics.StringValue(text) is not null => Eve.Macho.JsSemantics.StringValue(text)!,
        _ => ""
    };
}
