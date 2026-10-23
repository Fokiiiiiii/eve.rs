using Eve.Macho;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space;

[Service("fleetObjectHandler")]
public sealed partial class FleetObjectService(FleetStore store, Func<long> nowUnixMs, Func<long, ClientSession?>? sessionForCharacter = null, Func<IReadOnlyList<long>, CancellationToken, Task<PyValue>>? compositionForMembers = null, Func<long, long, string, ClientSession, CancellationToken, Task<PyValue?>>? loadFleetSetup = null, Func<long, long, PyValue, ClientSession, CancellationToken, Task<PyValue?>>? createFleetAdvert = null, Func<ClientSession, ClientSession, bool>? sameBubble = null, Func<long, CancellationToken, Task<IReadOnlyList<int>>>? memberSkillsForCharacter = null) : IBoundObjectReleaseHandler
{
    private readonly Binding _binding = new(nowUnixMs);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _fleetByOid = new(StringComparer.Ordinal);
    private static PyValue None => PyNone.Instance;

    public void OnBoundObjectReleased(string oid, ClientSession? session)
    {
        _binding.Release(oid);
        _fleetByOid.TryRemove(oid, out _);
    }

    [Method("MachoResolveObject")]
    public async ValueTask<PyValue> MachoResolveObject(ServiceCall call)
    {
        var fleetId = BoundFleetId(call) ?? Binding.PlaceOf(call.Arg(0)) ?? Number(call.Arg(0));
        return fleetId is > 0 && await store.ExistsAsync(fleetId.Value, call.Cancellation)
            ? new PyInt(MachoMessage.ProxyNodeId) : None;
    }

    [Method("MachoBindObject")]
    public async ValueTask<PyValue> MachoBindObject(ServiceCall call)
    {
        var fleetId = BoundFleetId(call) ?? Binding.PlaceOf(call.Arg(0)) ?? Number(call.Arg(0));
        if (fleetId is not > 0 || !await store.ExistsAsync(fleetId.Value, call.Cancellation)) return None;
        var (bound, oid) = _binding.Bind(call.Session);
        _fleetByOid[oid] = fleetId.Value;
        return Py.Tuple(bound, await Binding.NestedAsync(this, call, oid));
    }

    [Method("CreateFleet")]
    public async ValueTask<PyValue> CreateFleet(ServiceCall call)
    {
        var characterId = CharacterId(call.Session);
        if (characterId <= 0 || await store.MemberForCharacterAsync(characterId, call.Cancellation) is not null) return None;
        var fleetId = await store.CreateAsync(characterId, call.Cancellation);
        if (fleetId <= 0) return None;
        var (bound, oid) = _binding.Bind(call.Session);
        _fleetByOid[oid] = fleetId;
        SetSession(call.Session, fleetId, new FleetMemberSnapshot(characterId, FleetStore.NoUnit, FleetStore.NoUnit, FleetStore.Leader, FleetStore.CreatorJob));
        return bound;
    }

    [Method("Init")]
    public async ValueTask<PyValue> Init(ServiceCall call)
    {
        if (FleetId(call) is not { } fleetId) return None;
        var characterId = CharacterId(call.Session);
        if (characterId <= 0 ||
            await store.FleetIdForCharacterAsync(characterId, call.Cancellation) != fleetId)
            return None;

        var shipTypeId = call.Arg(0) is PyNone ? null : Number(call.Arg(0));
        if (shipTypeId is < 0) shipTypeId = null;
        await store.SetMemberShipTypeAsync(fleetId, characterId, shipTypeId, call.Cancellation);

        var setupName = Text(call.Arg(1));
        if (!string.IsNullOrWhiteSpace(setupName))
        {
            if (loadFleetSetup is null)
                throw new InvalidOperationException("Fleet setup loading requires the Host characterSettings callback.");
            await loadFleetSetup(fleetId, characterId, setupName, call.Session, call.Cancellation);
        }

        var advertData = call.Kwarg("adInfoData");
        if (advertData is null or PyNone) return None;
        if (createFleetAdvert is null)
            throw new InvalidOperationException("Fleet advert creation requires the Host FleetStore callback.");
        return await createFleetAdvert(fleetId, characterId, advertData, call.Session, call.Cancellation)
            ?? None;
    }

    [Method("GetFleetID")]
    public async ValueTask<PyValue> GetFleetID(ServiceCall call)
    {
        if (FleetId(call) is not { } id) return None;
        var characterId = CharacterId(call.Session);
        return characterId > 0 &&
               await store.FleetIdForCharacterAsync(characterId, call.Cancellation) == id
            ? new PyLong(id)
            : None;
    }

    [Method("GetInitState")]
    public async ValueTask<PyValue> GetInitState(ServiceCall call)
    {
        if (FleetId(call) is not { } id) return None;
        var characterId = CharacterId(call.Session);
        if (characterId <= 0 ||
            await store.FleetIdForCharacterAsync(characterId, call.Cancellation) != id ||
            await store.SnapshotAsync(id, call.Cancellation) is not { } state)
            return None;

        if (memberSkillsForCharacter is null)
            throw new InvalidOperationException("Fleet initialization requires the Host character skill projection.");
        var memberValues = new List<System.Collections.Generic.KeyValuePair<PyValue, PyValue>>(state.Members.Count);
        foreach (var member in state.Members)
        {
            call.Cancellation.ThrowIfCancellationRequested();
            var skillLevels = await memberSkillsForCharacter(member.CharacterId, call.Cancellation);
            memberValues.Add(new(new PyLong(member.CharacterId), MemberValue(member, skillLevels)));
        }
        var members = new PyDict(memberValues);
        var wings = new PyDict(state.Wings.Select(wing =>
            new System.Collections.Generic.KeyValuePair<PyValue, PyValue>(new PyLong(wing.WingId), WingValue(wing))).ToArray());
        return Py.KeyVal([
            ("fleetID", new PyLong(state.FleetId)),
            ("members", members),
            ("wings", wings),
            ("motd", new PyWString(state.Motd)),
            ("options", FleetOptionsValue(state))
        ]);
    }

    [Method("GetFleetMaxSize")]
    public async ValueTask<PyValue> GetFleetMaxSize(ServiceCall call)
    {
        if (FleetId(call) is not { } fleetId) return None;
        var characterId = CharacterId(call.Session);
        if (characterId <= 0 ||
            await store.FleetIdForCharacterAsync(characterId, call.Cancellation) != fleetId)
            return None;
        var maxMemberCount = await store.MaxMemberCountAsync(fleetId, call.Cancellation);
        return maxMemberCount is > 0 ? new PyInt(maxMemberCount) : None;
    }

    [Method("SetFleetMaxSize")]
    public async ValueTask<PyValue> SetFleetMaxSize(ServiceCall call)
    {
        var maxMemberCount = Number(call.Arg(0));
        if (FleetId(call) is { } fleetId && maxMemberCount is >= 1 and <= FleetStore.MaxMembers)
            await store.SetMaxMemberCountAsync(fleetId, CharacterId(call.Session), (int)maxMemberCount.Value,
                call.Cancellation);
        return None;
    }

    [Method("Invite")]
    public async ValueTask<PyValue> Invite(ServiceCall call)
    {
        if (FleetId(call) is not { } fleetId) return PyBool.False;
        var inviterId = CharacterId(call.Session);
        var inviteeId = Number(call.Arg(0)) ?? 0;
        if (inviteeId <= 0 || inviteeId == inviterId) return PyBool.False;
        var before = await store.SnapshotAsync(fleetId, call.Cancellation);
        var inviter = before?.Members.FirstOrDefault(member => member.CharacterId == inviterId);
        if (inviter is null || inviter.Role != FleetStore.Leader && !FleetStore.HasBossJob(inviter.Job))
            return PyBool.False;
        var inviteeSession = sessionForCharacter?.Invoke(inviteeId);
        if (inviteeSession is null)
        {
            throw MachoErrors.ThrowUserError(
                "FleetCandidateOffline",
                ("invitee", Py.Tuple(new PyInt(2), new PyLong(inviteeId))));
        }
        if (await store.FleetIdForCharacterAsync(inviteeId, call.Cancellation) is not null)
            throw MachoErrors.ThrowUserError("FleetError");

        var invited = await store.InviteAsync(fleetId, inviterId, inviteeId,
            Number(call.Arg(1)) ?? FleetStore.NoUnit, Number(call.Arg(2)) ?? FleetStore.NoUnit,
            FleetRole(call.Arg(3)), call.Cancellation);
        if (!invited) return PyBool.False;

        SendInviteNotification(inviteeSession, fleetId, inviterId, autoAccept: false);
        var after = await store.SnapshotAsync(fleetId, call.Cancellation);
        if (before is not null) PublishJoinRequestChanges(before, after);
        return PyBool.True;
    }

    [Method("MassInvite")]
    public async ValueTask<PyValue> MassInvite(ServiceCall call)
    {
        if (call.Arg(0) is not (PyList or PyTuple))
            throw MachoErrors.ThrowUserError("FleetError");
        var invitees = Sequence(call.Arg(0));
        if (invitees.Count > FleetStore.MaxMembers)
            throw MachoErrors.ThrowUserError("FleetError");
        var inviteeIds = invitees.Select(Number).ToArray();
        if (inviteeIds.Any(id => id is not > 0))
            throw MachoErrors.ThrowUserError("FleetError");
        if (FleetId(call) is not { } fleetId) return None;
        var inviterId = CharacterId(call.Session);
        var before = await store.SnapshotAsync(fleetId, call.Cancellation);
        var inviter = before?.Members.FirstOrDefault(member => member.CharacterId == inviterId);
        if (inviter is null || inviter.Role != FleetStore.Leader && !FleetStore.HasBossJob(inviter.Job))
            return new PyList([]);
        var wingId = Number(call.Arg(1)) ?? FleetStore.NoUnit;
        var squadId = Number(call.Arg(2)) ?? FleetStore.NoUnit;
        var role = FleetRole(call.Arg(3));
        List<PyValue> invited = [];
        foreach (var inviteeId in inviteeIds.Select(id => id!.Value).Distinct())
        {
            if (inviteeId == inviterId) continue;
            if (await store.FleetIdForCharacterAsync(inviteeId, call.Cancellation) is not null)
                throw MachoErrors.ThrowUserError("FleetError");
            if (!await store.InviteAsync(fleetId, inviterId, inviteeId, wingId, squadId, role, call.Cancellation))
                continue;
            SendInviteNotification(sessionForCharacter?.Invoke(inviteeId), fleetId, inviterId, autoAccept: false);
            invited.Add(new PyLong(inviteeId));
        }
        var after = await store.SnapshotAsync(fleetId, call.Cancellation);
        if (before is not null) PublishJoinRequestChanges(before, after);
        return new PyList(invited);
    }

    internal static void SendInviteNotification(ClientSession? invitee, long fleetId, long inviterId, bool autoAccept)
    {
        invitee?.SendNotification("OnFleetInvite", "clientID",
            new PyLong(fleetId), new PyLong(inviterId), new PyStr("AskJoinFleet"),
            Py.KeyVal([
                ("autoAccept", autoAccept ? PyBool.True : PyBool.False),
                ("name", new PyTuple([new PyInt(2), new PyLong(inviterId)]))
            ]));
    }

    [Method("Reconnect")]
    public ValueTask<PyValue> Reconnect(ServiceCall call) => RefreshMemberSessionAsync(call);

    [Method("FinishMove")]
    public ValueTask<PyValue> FinishMove(ServiceCall call) => RefreshMemberSessionAsync(call);

    private async ValueTask<PyValue> RefreshMemberSessionAsync(ServiceCall call)
    {
        if (FleetId(call) is not { } fleetId ||
            await store.SnapshotAsync(fleetId, call.Cancellation) is not { } snapshot ||
            snapshot.Members.FirstOrDefault(member => member.CharacterId == CharacterId(call.Session)) is not { } member)
            throw MachoErrors.ThrowUserError("FleetNotFound");
        SetSession(call.Session, fleetId, member);
        return PyBool.True;
    }

    [Method("AcceptInvite")]
    public async ValueTask<PyValue> AcceptInvite(ServiceCall call)
    {
        if (FleetId(call) is not { } fleetId) return None;
        var before = await store.SnapshotAsync(fleetId, call.Cancellation);
        var characterId = CharacterId(call.Session);
        var member = await store.JoinAsync(fleetId, characterId, Number(call.Arg(0)), call.Cancellation);
        if (member is null)
        {
            var current = await store.SnapshotAsync(fleetId, call.Cancellation);
            var advert = await store.ReadJoinAdvertPolicyAsync(fleetId, call.Session, call.Cancellation);
            var capacity = current?.MaxMemberCount ?? FleetStore.MaxMembers;
            if (advert?.JoinLimit is { } joinLimit)
                capacity = Math.Min(capacity, Math.Max(1, joinLimit));
            if (current is not null && current.Members.Count >= capacity)
                throw MachoErrors.ThrowUserError("FleetTooManyMembers");
            throw MachoErrors.ThrowUserError("FleetNotFound");
        }

        var after = await store.SnapshotAsync(fleetId, call.Cancellation);
        if (before is not null) PublishJoinRequestChanges(before, after);
        call.Session.SendNotification("OnFleetRespawnPointsUpdate", "clientID", new PyList([]));
        SetSession(call.Session, fleetId, member);
        call.Session.SendNotification("OnFleetStateChange", "charid",
            Py.KeyVal([("targetTags", new PyDict([]))]));
        if (before is not null) await PublishMemberChangesAsync(call.Session, fleetId, before, after, call.Cancellation);
        return PyBool.True;
    }

    [Method("RejectInvite")]
    public async ValueTask<PyValue> RejectInvite(ServiceCall call)
    {
        if (FleetId(call) is { } fleetId) await store.RejectInviteAsync(fleetId, CharacterId(call.Session), call.Cancellation);
        return None;
    }

    [Method("LeaveFleet")]
    public async ValueTask<PyValue> LeaveFleet(ServiceCall call)
    {
        if (FleetId(call) is not { } fleetId) return None;
        var before = await store.SnapshotAsync(fleetId, call.Cancellation);
        if (await store.LeaveAsync(fleetId, CharacterId(call.Session), call.Cancellation) is null) return None;
        ClearSession(call.Session);
        var after = await store.SnapshotAsync(fleetId, call.Cancellation);
        if (before is not null) await PublishMemberChangesAsync(call.Session, fleetId, before, after, call.Cancellation);
        return None;
    }

    [Method("DisbandFleet")]
    public async ValueTask<PyValue> DisbandFleet(ServiceCall call)
    {
        if (FleetId(call) is not { } fleetId) return None;
        var before = await store.SnapshotAsync(fleetId, call.Cancellation);
        if (before is null || !await store.DisbandAsync(fleetId, CharacterId(call.Session), call.Cancellation)) return None;
        var after = await store.SnapshotAsync(fleetId, call.Cancellation);
        await PublishMemberChangesAsync(call.Session, fleetId, before, after, call.Cancellation);
        return None;
    }

    [Method("CreateWing")]
    public async ValueTask<PyValue> CreateWing(ServiceCall call) =>
        FleetId(call) is { } id && await store.CreateWingAsync(id, CharacterId(call.Session), call.Cancellation) is var wing && wing > 0
            ? new PyLong(wing) : None;

    [Method("DeleteWing")]
    public async ValueTask<PyValue> DeleteWing(ServiceCall call)
    {
        if (FleetId(call) is { } id) await store.DeleteWingAsync(id, CharacterId(call.Session), Number(call.Arg(0)) ?? 0, call.Cancellation);
        return None;
    }

    [Method("CreateSquad")]
    public async ValueTask<PyValue> CreateSquad(ServiceCall call) =>
        FleetId(call) is { } id && await store.CreateSquadAsync(id, CharacterId(call.Session), Number(call.Arg(0)) ?? 0, call.Cancellation) is var squad && squad > 0
            ? new PyLong(squad) : None;

    [Method("DeleteSquad")]
    public async ValueTask<PyValue> DeleteSquad(ServiceCall call)
    {
        if (FleetId(call) is { } id) await store.DeleteSquadAsync(id, CharacterId(call.Session), Number(call.Arg(0)) ?? 0, call.Cancellation);
        return None;
    }

    [Method("ChangeWingName")]
    public async ValueTask<PyValue> ChangeWingName(ServiceCall call)
    {
        if (FleetId(call) is { } id) await store.RenameWingAsync(id, CharacterId(call.Session), Number(call.Arg(0)) ?? 0, Text(call.Arg(1)), call.Cancellation);
        return None;
    }

    [Method("ChangeSquadName")]
    public async ValueTask<PyValue> ChangeSquadName(ServiceCall call)
    {
        if (FleetId(call) is { } id) await store.RenameSquadAsync(id, CharacterId(call.Session), Number(call.Arg(0)) ?? 0, Text(call.Arg(1)), call.Cancellation);
        return None;
    }

    [Method("MoveMember")]
    public async ValueTask<PyValue> MoveMember(ServiceCall call)
    {
        if (FleetId(call) is not { } fleetId) return None;
        var before = await store.SnapshotAsync(fleetId, call.Cancellation);
        if (before is null) return None;
        var characterId = Number(call.Arg(0)) ?? 0;
        var wingId = Number(call.Arg(1)) ?? FleetStore.NoUnit;
        var squadId = Number(call.Arg(2)) ?? FleetStore.NoUnit;
        var role = FleetRole(call.Arg(3));
        if (!await store.MoveMemberAsync(fleetId, CharacterId(call.Session), characterId, wingId, squadId, role, call.Cancellation))
            return None;
        var after = await store.SnapshotAsync(fleetId, call.Cancellation);
        await PublishMemberChangesAsync(call.Session, fleetId, before, after, call.Cancellation);
        return None;
    }

    [Method("MassMoveMembers")]
    public async ValueTask<PyValue> MassMoveMembers(ServiceCall call)
    {
        if (call.Arg(0) is not (PyList or PyTuple))
            throw MachoErrors.ThrowUserError("FleetError");
        var memberIds = Sequence(call.Arg(0));
        if (memberIds.Count > FleetStore.MaxMembers)
            throw MachoErrors.ThrowUserError("FleetError");
        var requestedMemberIds = memberIds.Select(Number).ToArray();
        if (requestedMemberIds.Any(id => id is not > 0))
            throw MachoErrors.ThrowUserError("FleetError");
        if (FleetId(call) is not { } fleetId) return None;
        var before = await store.SnapshotAsync(fleetId, call.Cancellation);
        if (before is null) return None;
        var wingId = Number(call.Arg(1)) ?? FleetStore.NoUnit;
        var squadId = Number(call.Arg(2)) ?? FleetStore.NoUnit;
        var role = FleetRole(call.Arg(3));
        var ids = requestedMemberIds.Select(id => id!.Value).Distinct().ToArray();
        if (!await store.MassMoveMembersAsync(fleetId, CharacterId(call.Session), ids, wingId, squadId, role, call.Cancellation))
            return None;
        var after = await store.SnapshotAsync(fleetId, call.Cancellation);
        await PublishMemberChangesAsync(call.Session, fleetId, before, after, call.Cancellation);
        return None;
    }

    [Method("MakeLeader")]
    public async ValueTask<PyValue> MakeLeader(ServiceCall call)
    {
        if (FleetId(call) is not { } fleetId) return None;
        var before = await store.SnapshotAsync(fleetId, call.Cancellation);
        if (before is null) return None;
        var targetId = Number(call.Arg(0)) ?? 0;
        if (!await store.MakeLeaderAsync(fleetId, CharacterId(call.Session), targetId, call.Cancellation))
            return None;
        var after = await store.SnapshotAsync(fleetId, call.Cancellation);
        await PublishMemberChangesAsync(call.Session, fleetId, before, after, call.Cancellation);
        return None;
    }

    [Method("KickMember")]
    public async ValueTask<PyValue> KickMember(ServiceCall call)
    {
        if (FleetId(call) is not { } fleetId) return None;
        var before = await store.SnapshotAsync(fleetId, call.Cancellation);
        if (before is null ||
            !await store.KickMemberAsync(fleetId, CharacterId(call.Session), Number(call.Arg(0)) ?? 0, call.Cancellation))
            return None;
        var after = await store.SnapshotAsync(fleetId, call.Cancellation);
        await PublishMemberChangesAsync(call.Session, fleetId, before, after, call.Cancellation);
        return None;
    }

    [Method("SetAutoJoinSquadID")]
    public async ValueTask<PyValue> SetAutoJoinSquadID(ServiceCall call)
    {
        if (FleetId(call) is { } id)
        {
            var raw = call.Arg(0);
            var squadId = raw is PyNone ? null : Number(raw);
            await store.SetAutoJoinSquadAsync(id, CharacterId(call.Session), squadId, call.Cancellation);
        }
        return None;
    }

    [Method("GetJoinRequests")]
    public async ValueTask<PyValue> GetJoinRequests(ServiceCall call)
    {
        if (FleetId(call) is not { } id ||
            await store.SnapshotAsync(id, call.Cancellation) is not { } state)
            return None;

        var characterId = CharacterId(call.Session);
        var requester = state.Members.SingleOrDefault(member => member.CharacterId == characterId);
        if (requester is null ||
            requester.Role != FleetStore.Leader && !FleetStore.HasBossJob(requester.Job))
            return None;

        return JoinRequestsValue(state.JoinRequests);
    }

    [Method("RejectJoinRequest")]
    public async ValueTask<PyValue> RejectJoinRequest(ServiceCall call)
    {
        if (FleetId(call) is not { } fleetId) return None;
        var before = await store.SnapshotAsync(fleetId, call.Cancellation);
        if (before is null ||
            !await store.RejectJoinRequestAsync(fleetId, CharacterId(call.Session),
                Number(call.Arg(0)) ?? 0, call.Cancellation))
            return None;
        PublishJoinRequestChanges(before, await store.SnapshotAsync(fleetId, call.Cancellation));
        return None;
    }

    private long? FleetId(ServiceCall call) => call.BoundObjectId is { } oid && _fleetByOid.TryGetValue(oid, out var id) ? id : null;
    private long? BoundFleetId(ServiceCall call) => FleetId(call);

    private PyValue MemberValue(FleetMemberSnapshot member, IReadOnlyList<int> skillLevels)
    {
        if (skillLevels.Count != 3 || skillLevels.Any(level => level is < 0 or > 5))
            throw new InvalidOperationException("Fleet member skills must contain the three command skill levels.");
        var session = sessionForCharacter?.Invoke(member.CharacterId);
        var solarSystemId = session?.GetInt("solarsystemid2") ?? session?.GetInt("solarsystemid");
        var fileTime = (nowUnixMs() + 11_644_473_600_000L) * 10_000L;
        return Py.KeyVal([
            ("squadID", new PyLong(member.SquadId)),
            ("wingID", new PyLong(member.WingId)),
            ("skills", new PyList(skillLevels.Select(level => (PyValue)new PyInt(level)).ToArray())),
            ("timestamp", new PyLong(fileTime)),
            ("stationID", NullableLong(session?.GetInt("stationid"))),
            ("clientID", session is { ClientId: > 0 } ? new PyLong(session.ClientId) : PyNone.Instance),
            ("job", new PyInt(member.Job)),
            ("role", new PyInt(member.Role)),
            ("shipTypeID", NullableLong(member.ShipTypeId)),
            ("solarSystemID", NullableLong(solarSystemId)),
            ("memberOptOuts", MemberOptOutsValue(member)),
            ("charID", new PyLong(member.CharacterId))
        ]);
    }

    internal static FleetJoinRequest JoinRequestFromSession(ClientSession session)
    {
        var securityStatus = session.Get("securityStatus") switch
        {
            PyReal real when double.IsFinite(real.Value) => real.Value,
            PyInt number => number.Value,
            PyLong number => number.Value,
            _ => 0
        };
        return new FleetJoinRequest(CharacterId(session),
            session.GetInt("corpid") ?? session.GetInt("corporationID"),
            session.GetInt("allianceid") ?? session.GetInt("allianceID"),
            session.GetInt("warfactionid") ?? session.GetInt("warFactionID"),
            securityStatus);
    }

    internal static PyValue JoinRequestValue(FleetJoinRequest request, ClientSession? session)
    {
        var character = session is not null && CharacterId(session) == request.CharacterId ? session : null;
        var corporationId = character?.GetInt("corpid") ?? character?.GetInt("corporationID") ?? request.CorporationId;
        var allianceId = character?.GetInt("allianceid") ?? character?.GetInt("allianceID") ?? request.AllianceId;
        var warFactionId = character?.GetInt("warfactionid") ?? character?.GetInt("warFactionID") ?? request.WarFactionId;
        var securityStatus = request.SecurityStatus;
        if (character?.Get("securityStatus") is { } value)
            securityStatus = value switch
            {
                PyReal real when double.IsFinite(real.Value) => real.Value,
                PyInt number => number.Value,
                PyLong number => number.Value,
                _ => securityStatus
            };
        return Py.KeyVal([
            ("charID", new PyLong(request.CharacterId)),
            ("corpID", NullableLong(corporationId)),
            ("allianceID", NullableLong(allianceId)),
            ("warFactionID", NullableLong(warFactionId)),
            ("securityStatus", new PyReal(securityStatus))
        ]);
    }

    internal static long? BossCharacterId(FleetSnapshot snapshot) =>
        snapshot.Members.FirstOrDefault(member => FleetStore.HasBossJob(member.Job))?.CharacterId;

    private PyValue JoinRequestsValue(IReadOnlyList<FleetJoinRequest> requests) => new PyDict(
        requests.OrderBy(request => request.CharacterId).Select(request =>
            new System.Collections.Generic.KeyValuePair<PyValue, PyValue>(
                new PyLong(request.CharacterId),
                JoinRequestValue(request, sessionForCharacter?.Invoke(request.CharacterId)))).ToArray());

    private static PyValue WingValue(FleetWingSnapshot wing) => Py.KeyVal([
        ("wingID", new PyLong(wing.WingId)),
        ("name", new PyWString(wing.Name)),
        ("squads", new PyDict(wing.Squads.Select(squad =>
            new System.Collections.Generic.KeyValuePair<PyValue, PyValue>(new PyLong(squad.SquadId),
                Py.KeyVal([("squadID", new PyLong(squad.SquadId)), ("name", new PyWString(squad.Name))]))).ToArray()))
    ]);

    private static long CharacterId(ClientSession session) => session.CharacterId ?? session.GetInt("charid") ?? 0;
    private static void SetSession(ClientSession session, long fleetId, FleetMemberSnapshot member)
    {
        session.Change(
            ("fleetid", new PyLong(fleetId)),
            ("fleetrole", new PyInt(member.Role)),
            ("wingid", member.WingId > 0 ? new PyLong(member.WingId) : PyNone.Instance),
            ("squadid", member.SquadId > 0 ? new PyLong(member.SquadId) : PyNone.Instance));
    }

    private static void ClearSession(ClientSession session)
    {
        session.Change(
            ("fleetid", PyNone.Instance),
            ("fleetrole", PyNone.Instance),
            ("wingid", PyNone.Instance),
            ("squadid", PyNone.Instance));
    }

    internal static int FleetRole(PyValue value)
    {
        var role = Number(value) ?? FleetStore.Member;
        if (role < int.MinValue || role > int.MaxValue)
            throw MachoErrors.ThrowUserError("FLEET_ROLE_OUT_OF_RANGE");
        return (int)role;
    }

    private static long? Number(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        PyReal number when double.IsFinite(number.Value) &&
            number.Value >= -9_223_372_036_854_775_808d &&
            number.Value < 9_223_372_036_854_775_808d &&
            Math.Truncate(number.Value) == number.Value => (long)number.Value,
        PyStr text when long.TryParse(text.Value, out var parsed) => parsed,
        PyWString text when long.TryParse(text.Value, out var parsed) => parsed,
        _ => null
    };

    private static string Text(PyValue value) => value switch
    {
        var text when Eve.Macho.JsSemantics.StringValue(text) is not null => Eve.Macho.JsSemantics.StringValue(text)!,
        _ => ""
    };

    private static IReadOnlyList<PyValue> Sequence(PyValue value) => value switch
    {
        PyList list => list.Items,
        PyTuple tuple => tuple.Items,
        _ => []
    };
}

public sealed partial class FleetObjectService
{
private static readonly System.Collections.Generic.HashSet<string> BroadcastNames = new(StringComparer.Ordinal)
    {
        "EnemySpotted", "HealArmor", "HealShield", "HealCapacitor", "InPosition", "NeedBackup",
        "HoldPosition", "Location", "TravelTo", "JumpBeacon", "Target", "RepTarget", "WarpTo",
        "AlignTo", "JumpTo"
    };

    [Method("GetWings")]
    public async ValueTask<PyValue> GetWings(ServiceCall call)
    {
        var fleetId = FleetId(call);
        var characterId = CharacterId(call.Session);
        if (fleetId is not { } id || characterId <= 0 ||
            await store.FleetIdForCharacterAsync(characterId, call.Cancellation) != id ||
            await store.SnapshotAsync(id, call.Cancellation) is not { } snapshot)
            return new PyDict([]);

        return new PyDict(snapshot.Wings.Select(wing =>
            new System.Collections.Generic.KeyValuePair<PyValue, PyValue>(new PyLong(wing.WingId), WingValue(wing))).ToArray());
    }

    [Method("LoadFleetSetup")]
    public async ValueTask<PyValue> LoadFleetSetup(ServiceCall call)
    {
        if (FleetId(call) is not { } fleetId ||
            await store.SnapshotAsync(fleetId, call.Cancellation) is not { } snapshot)
            return None;
        var characterId = CharacterId(call.Session);
        var member = snapshot.Members.FirstOrDefault(item => item.CharacterId == characterId);
        if (member is null || member.Role != FleetStore.Leader && !FleetStore.HasBossJob(member.Job))
            return None;
        var setupName = Text(call.Arg(0));
        if (string.IsNullOrWhiteSpace(setupName)) return None;
        if (loadFleetSetup is null)
            throw new InvalidOperationException("Fleet setup loading requires the Host characterSettings callback.");
        await loadFleetSetup(fleetId, characterId, setupName, call.Session, call.Cancellation);
        return None;
    }

    [Method("SetTakesFleetWarp")]
    public async ValueTask<PyValue> SetTakesFleetWarp(ServiceCall call)
    {
        var value = BooleanValue(call.Arg(0));
        if (value is not null) await SetMemberOptionsAsync(call, acceptsFleetWarp: value);
        return None;
    }

    [Method("SetAcceptsConduitJumpsValue")]
    public async ValueTask<PyValue> SetAcceptsConduitJumpsValue(ServiceCall call)
    {
        var value = BooleanValue(call.Arg(0));
        if (value is not null) await SetMemberOptionsAsync(call, acceptsConduitJumps: value);
        return None;
    }

    [Method("SetAcceptsRegroupValue")]
    public async ValueTask<PyValue> SetAcceptsRegroupValue(ServiceCall call)
    {
        var value = BooleanValue(call.Arg(0));
        if (value is not null) await SetMemberOptionsAsync(call, acceptsFleetRegroups: value);
        return None;
    }

    [Method("SetOptions")]
    public async ValueTask<PyValue> SetOptions(ServiceCall call)
    {
        if (FleetId(call) is not { } fleetId ||
            await store.SnapshotAsync(fleetId, call.Cancellation) is not { } before ||
            BooleanValue(Field(call.Arg(0), "isFreeMove") ?? PyNone.Instance) is not { } isFreeMove ||
            !await store.SetFreeMoveAsync(fleetId, CharacterId(call.Session), isFreeMove, call.Cancellation))
            return None;

        var after = await store.SnapshotAsync(fleetId, call.Cancellation);
        if (after is not null && after.IsFreeMove != before.IsFreeMove)
            NotifyFleetOptionsChanged(fleetId, before, after, call.Session);
        return None;
    }

    [Method("GetFleetComposition")]
    public async ValueTask<PyValue> GetFleetComposition(ServiceCall call)
    {
        if (FleetId(call) is not { } fleetId ||
            await store.SnapshotAsync(fleetId, call.Cancellation) is not { } snapshot)
            return new PyList([]);

        var viewer = snapshot.Members.FirstOrDefault(member => member.CharacterId == CharacterId(call.Session));
        if (viewer is null) return new PyList([]);
        var visible = snapshot.Members.Where(member =>
            FleetStore.HasBossJob(viewer.Job) || viewer.Role == FleetStore.Leader ||
            viewer.Role == FleetStore.WingCommander && member.WingId == viewer.WingId ||
            viewer.Role == FleetStore.SquadCommander && member.SquadId == viewer.SquadId).ToArray();
        var characterIds = visible.Select(member => member.CharacterId).ToArray();
        if (compositionForMembers is not null)
            return await compositionForMembers(characterIds, call.Cancellation);

        return new PyList(visible.Select(member =>
        {
            var session = sessionForCharacter?.Invoke(member.CharacterId);
            var shipTypeId = session?.GetInt("shiptypeid") ?? member.ShipTypeId;
            return (PyValue)Py.KeyVal([
                ("characterID", new PyLong(member.CharacterId)),
                ("solarSystemID", session?.GetInt("solarsystemid2") is { } systemId ? new PyLong(systemId) : PyNone.Instance),
                ("stationID", session?.GetInt("stationid") is { } stationId ? new PyLong(stationId) : PyNone.Instance),
                ("shipTypeID", shipTypeId is { } typeId ? new PyLong(typeId) : PyNone.Instance),
                ("skills", new PyList([])),
                ("skillIDs", new PyList([]))
            ]);
        }).ToArray());
    }

    [Method("SendBroadcast")]
    public async ValueTask<PyValue> SendBroadcast(ServiceCall call)
    {
        if (FleetId(call) is not { } fleetId ||
            await store.SnapshotAsync(fleetId, call.Cancellation) is not { } snapshot)
            return None;
        var senderId = CharacterId(call.Session);
        var sender = snapshot.Members.FirstOrDefault(member => member.CharacterId == senderId);
        var name = Text(call.Arg(0));
        var scope = Number(call.Arg(1));
        if (sender is null || !BroadcastNames.Contains(name) || scope is not (1 or 2 or 3)) return None;

        var systemId = call.Session.GetInt("solarsystemid2") ?? call.Session.GetInt("solarsystemid");
        var range = name switch
        {
            "Target" or "RepTarget" or "HealArmor" or "HealShield" or "HealCapacitor" => 2,
            "WarpTo" or "AlignTo" or "JumpTo" => 1,
            _ => 0
        };
        foreach (var member in snapshot.Members)
        {
            if (!ShouldReceiveBroadcast(member, sender, (int)scope.Value)) continue;
            var recipient = member.CharacterId == senderId
                ? call.Session
                : sessionForCharacter?.Invoke(member.CharacterId);
            if (recipient?.CharacterId != member.CharacterId) continue;
            if (range == 1 && (systemId is null ||
                (recipient.GetInt("solarsystemid2") ?? recipient.GetInt("solarsystemid")) != systemId)) continue;
            if (range == 2)
            {
                var sameGrid = sameBubble?.Invoke(call.Session, recipient) ??
                    (call.Session.GetInt("ballparkid") is { } bubbleId && recipient.GetInt("ballparkid") == bubbleId);
                if (!sameGrid) continue;
            }

            recipient.SendNotification("OnFleetBroadcast", "fleetid",
                new PyStr(name), new PyInt((int)scope.Value), new PyLong(senderId),
                systemId is { } currentSystem ? new PyLong(currentSystem) : PyNone.Instance,
                WireId(Number(call.Arg(2))), WireId(Number(call.Arg(3))));
        }
        return None;
    }

    [Method("UpdateMemberInfo")]
    public async ValueTask<PyValue> UpdateMemberInfo(ServiceCall call)
    {
        if (FleetId(call) is not { } fleetId) return None;
        var characterId = CharacterId(call.Session);
        var before = await store.SnapshotAsync(fleetId, call.Cancellation);
        var oldMember = before?.Members.FirstOrDefault(member => member.CharacterId == characterId);
        if (oldMember is null) return None;
        var shipTypeId = call.Arg(0) is PyNone ? null : Number(call.Arg(0));
        if (shipTypeId is < 0) shipTypeId = null;
        var updated = await store.SetMemberShipTypeAsync(fleetId, characterId, shipTypeId, call.Cancellation);
        var after = await store.SnapshotAsync(fleetId, call.Cancellation);
        if (updated is not null && after is not null && updated != oldMember)
            NotifyMemberChanged(fleetId, oldMember, updated, after, call.Session);
        return None;
    }

    [Method("SetMotdEx")]
    public async ValueTask<PyValue> SetMotdEx(ServiceCall call)
    {
        if (FleetId(call) is not { } fleetId) return None;
        var motd = Text(call.Arg(0));
        if (motd.Length > 4096)
            throw MachoErrors.ThrowUserError("FleetError");
        if (!await store.SetMotdAsync(fleetId, CharacterId(call.Session), motd, call.Cancellation)) return None;
        var snapshot = await store.SnapshotAsync(fleetId, call.Cancellation);
        if (snapshot is not null)
            foreach (var member in snapshot.Members)
                (member.CharacterId == CharacterId(call.Session) ? call.Session : sessionForCharacter?.Invoke(member.CharacterId))
                    ?.SendNotification("OnFleetMotdChanged", "fleetid", new PyWString(motd), PyBool.True);
        return None;
    }

    [Method("GetMotd")]
    public async ValueTask<PyValue> GetMotd(ServiceCall call)
    {
        if (FleetId(call) is not { } fleetId ||
            await store.FleetIdForCharacterAsync(CharacterId(call.Session), call.Cancellation) != fleetId)
            return None;
        return new PyWString(await store.GetMotdAsync(fleetId, call.Cancellation) ?? "");
    }

    private async ValueTask<PyValue> SetMemberOptionsAsync(ServiceCall call,
        bool? acceptsFleetWarp = null, bool? acceptsConduitJumps = null, bool? acceptsFleetRegroups = null)
    {
        if (FleetId(call) is not { } fleetId) return None;
        var characterId = CharacterId(call.Session);
        var before = await store.SnapshotAsync(fleetId, call.Cancellation);
        var oldMember = before?.Members.FirstOrDefault(member => member.CharacterId == characterId);
        if (oldMember is null) return None;
        var updated = await store.SetMemberOptionsAsync(fleetId, characterId, acceptsFleetWarp,
            acceptsConduitJumps, acceptsFleetRegroups, call.Cancellation);
        var after = await store.SnapshotAsync(fleetId, call.Cancellation);
        if (updated is not null && after is not null && updated != oldMember)
            NotifyMemberChanged(fleetId, oldMember, updated, after, call.Session);
        return None;
    }

    private void NotifyFleetOptionsChanged(long fleetId, FleetSnapshot before, FleetSnapshot after, ClientSession issuer)
    {
        var oldOptions = FleetOptionsValue(before);
        var newOptions = FleetOptionsValue(after);
        foreach (var member in after.Members)
            (member.CharacterId == CharacterId(issuer) ? issuer : sessionForCharacter?.Invoke(member.CharacterId))
                ?.SendNotification("OnFleetOptionsChanged", "fleetid", oldOptions, newOptions);
    }

    private void NotifyMemberChanged(long fleetId, FleetMemberSnapshot oldMember, FleetMemberSnapshot newMember,
        FleetSnapshot after, ClientSession issuer)
    {
        var recipients = after.Members
            .Select(member => member.CharacterId == CharacterId(issuer) ? issuer : sessionForCharacter?.Invoke(member.CharacterId))
            .Where(session => session is not null)
            .Cast<ClientSession>()
            .Distinct()
            .ToArray();
        foreach (var recipient in recipients)
            recipient.SendNotification("OnFleetMemberChanged", "fleetid",
                new PyLong(newMember.CharacterId), new PyLong(fleetId),
                new PyLong(oldMember.WingId), new PyLong(oldMember.SquadId),
                new PyInt(oldMember.Role), new PyInt(oldMember.Job), MemberOptOutsValue(oldMember),
                new PyLong(newMember.WingId), new PyLong(newMember.SquadId),
                new PyInt(newMember.Role), new PyInt(newMember.Job), MemberOptOutsValue(newMember),
                after.Members.Count == 1 ? PyBool.True : PyBool.False);
    }

    private static PyValue FleetOptionsValue(FleetSnapshot snapshot) => Py.KeyVal([
        ("isFreeMove", snapshot.IsFreeMove ? PyBool.True : PyBool.False),
        ("isRegistered", snapshot.IsRegistered ? PyBool.True : PyBool.False),
        ("autoJoinSquadID", snapshot.AutoJoinSquadId is { } squadId ? new PyLong(squadId) : PyNone.Instance)
    ]);

    private static PyValue MemberOptOutsValue(FleetMemberSnapshot member) => Py.KeyVal([
        ("acceptsFleetWarp", member.AcceptsFleetWarp ? PyBool.True : PyBool.False),
        ("acceptsConduitJumps", member.AcceptsConduitJumps ? PyBool.True : PyBool.False),
        ("acceptsFleetRegroups", member.AcceptsFleetRegroups ? PyBool.True : PyBool.False)
    ]);

    private static bool ShouldReceiveBroadcast(FleetMemberSnapshot recipient, FleetMemberSnapshot sender, int scope)
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

    private static bool? BooleanValue(PyValue value) => value switch
    {
        PyBool boolean => boolean.Value,
        PyInt number => number.Value != 0,
        _ => null
    };

    private static PyValue? Field(PyValue value, string name)
    {
        if (value is not PyDict dict) return null;
        foreach (var (key, fieldValue) in dict.Entries)
            if (Eve.Macho.JsSemantics.StringValue(key) == name) return fieldValue;
        return null;
    }

    private static PyValue WireId(long? id) => id is { } value ? new PyLong(value) : PyNone.Instance;

    private async Task PublishMemberChangesAsync(
        ClientSession issuer,
        long fleetId,
        FleetSnapshot before,
        FleetSnapshot? after,
        CancellationToken cancellationToken)
    {
        var oldMembers = before.Members.ToDictionary(member => member.CharacterId);
        var newMembers = after?.Members.ToDictionary(member => member.CharacterId) ?? [];
        ClientSession? LiveSession(long characterId)
        {
            var session = characterId == CharacterId(issuer) ? issuer : sessionForCharacter?.Invoke(characterId);
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
            if (LiveSession(change.CharacterId) is not { } session) continue;
            if (change.New is null) ClearSession(session);
            else if (change.Old is null) SetSession(session, fleetId, change.New);
        }

        foreach (var change in changes)
        {
            foreach (var recipient in recipients)
            {
                var recipientId = CharacterId(recipient);
                if (recipientId <= 0 || !oldMembers.ContainsKey(recipientId) && !newMembers.ContainsKey(recipientId))
                    continue;

                if (change.Old is null && change.New is { } joined)
                {
                    if (memberSkillsForCharacter is null)
                        throw new InvalidOperationException("Fleet join notifications require the Host character skill projection.");
                    cancellationToken.ThrowIfCancellationRequested();
                    var skillLevels = await memberSkillsForCharacter(joined.CharacterId, cancellationToken);
                    recipient.SendNotification("OnFleetJoin", "fleetid", MemberValue(joined, skillLevels));
                }
                else if (change.New is null)
                    recipient.SendNotification("OnFleetLeave", "fleetid", new PyLong(change.CharacterId));
                else
                    recipient.SendNotification("OnFleetMemberChanged", "fleetid",
                        new PyLong(change.CharacterId), new PyLong(fleetId),
                        NullableLong(change.Old?.WingId), NullableLong(change.Old?.SquadId),
                        NullableInt(change.Old?.Role), NullableInt(change.Old?.Job),
                        change.Old is null ? PyNone.Instance : MemberOptOutsValue(change.Old),
                        NullableLong(change.New.WingId), NullableLong(change.New.SquadId),
                        NullableInt(change.New.Role), NullableInt(change.New.Job), MemberOptOutsValue(change.New),
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

    private void PublishJoinRequestChanges(FleetSnapshot before, FleetSnapshot? after)
    {
        var requests = after?.JoinRequests ?? [];
        if (before.JoinRequests.SequenceEqual(requests) ||
            after is null && before.JoinRequests.Count == 0)
            return;
        var bossId = BossCharacterId(after ?? before);
        if (bossId is { } id && sessionForCharacter?.Invoke(id) is { } session && CharacterId(session) == id)
            session.SendNotification("OnJoinRequestUpdate", "fleetid", JoinRequestsValue(requests));
    }

    private static PyValue NullableLong(long? value) => value is { } id ? new PyLong(id) : PyNone.Instance;
    private static PyValue NullableInt(int? value) => value is { } number ? new PyInt(number) : PyNone.Instance;
}
