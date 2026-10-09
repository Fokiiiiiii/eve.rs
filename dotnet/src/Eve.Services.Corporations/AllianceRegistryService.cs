using System.Collections.Concurrent;
using Eve.Characters;
using Eve.Corporations;
using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Corporations;

// Wire contracts follow EveJS v0.12.9 allianceRegistryRuntime.js and allianceViewState.js (828823a75f211fdecb945b087a26c225f111491f).

public sealed record AllianceSovSnapshot(
    IReadOnlyDictionary<long,long?> ClaimAllianceIdsBySolarSystemId,
    IReadOnlyDictionary<long,(long? AllianceId,long? HubId)> HubsBySolarSystemId,
    long? NextVulnerabilityWindowEndFileTime);

[Service("allianceRegistry")]
public sealed partial class AllianceRegistryService(CorporationStore store,PgCharacterStore characters,Func<long> now,AllianceApplicationScheduleCallback? scheduleAllianceApplication = null,Action? wakeAllianceApplicationScheduler = null,Eve.Relations.RelationStore? relations = null,Func<long,CancellationToken,Task<AllianceSovSnapshot?>>? readSovSnapshot = null,CharacterSessions? characterSessions = null) : IBoundObjectReleaseHandler
{
public AllianceRegistryService(CorporationStore store, PgCharacterStore characters, Func<long> now,
        Func<long, CancellationToken, Task>? legacyAffiliationChanged)
        : this(store, characters, now)
    {
        _ = legacyAffiliationChanged;
    }
    private readonly ConcurrentDictionary<string,long> _alliancesByObject = new(StringComparer.Ordinal);

    private static readonly string[] AllianceHeader =
    [
        "allianceID","allianceName","shortName","executorCorpID","creatorCorpID","creatorCharID","warFactionID",
        "description","url","startDate","memberCount","dictatorial","allowWar","currentCapital","currentPrimeHour",
        "newPrimeHour","newPrimeHourValidAfter","deleted"
    ];

    internal static (string Key,PyValue Value)[] AllianceFields(
        Alliance a,
        long memberCount,
        short currentPrimeHour = 0,
        short newPrimeHour = 0,
        long newPrimeHourValidAfter = 0,
        long? currentCapitalSystemId = null) =>
    [
        ("allianceID",new PyInt(a.Id)),("allianceName",new PyStr(a.Name)),("shortName",new PyStr(a.ShortName)),
        ("executorCorpID",CorporationWire.Id(a.ExecutorCorporationId)),("creatorCorpID",new PyInt(a.CreatorCorporationId)),
        ("creatorCharID",new PyInt(a.CreatorCharacterId)),("warFactionID",PyNone.Instance),("description",new PyStr(a.Description)),
        ("url",string.IsNullOrWhiteSpace(a.Url) ? PyNone.Instance : new PyStr(a.Url)),("startDate",CorporationWire.Time(a.CreatedMs)),
        ("memberCount",new PyInt(memberCount)),("dictatorial",new PyInt(a.Dictatorial ? 1 : 0)),("allowWar",new PyInt(0)),
        ("currentCapital",currentCapitalSystemId is { } capital ? new PyLong(capital) : PyNone.Instance),
        ("currentPrimeHour",new PyInt(currentPrimeHour)),("newPrimeHour",new PyInt(newPrimeHour)),
        ("newPrimeHourValidAfter",new PyLong(newPrimeHourValidAfter)),("deleted",new PyInt(a.Deleted ? 1 : 0))
    ];

    private static PyValue AllianceRow(Alliance a,long memberCount)
    {
        var fields = AllianceFields(a,memberCount);
        return new PyObject(new PyStr("util.Row"),Py.Dict(
            ("header",Py.Tuple([.. AllianceHeader.Select(name => (PyValue)new PyStr(name))])),
            ("line",Py.Tuple([.. fields.Select(field => field.Value)]))));
    }

    private long AllianceMembershipDays(long startedMs)
    {
        var elapsed = now() - startedMs;
        return elapsed > 0 ? elapsed / 86400000 : 0;
    }

    private static long Number(PyValue value) => value switch { PyInt n => n.Value,PyLong n => n.Value,PyTuple { Items.Count:>0 } t => Number(t.Items[0]),_ => 0 };
    private async Task<long> Id(ServiceCall call)
    {
        if (Number(call.Arg(0)) is > 0 and var requested) return requested;
        if (call.BoundObjectId is { } oid && _alliancesByObject.TryGetValue(oid,out var bound)) return bound;
        return call.Session.CharacterId is { } id ? (await characters.GetAsync(id,call.Cancellation))?.AllianceId ?? 0 : 0;
    }
    [Method("IsAllianceLocal")]
    public async ValueTask<PyValue> IsAllianceLocal(ServiceCall call) =>
        new PyInt(await store.AllianceAsync(await Id(call),call.Cancellation) is null ? 0 : 1);

    [Method("GetAlliance")]
    public async ValueTask<PyValue> GetAlliance(ServiceCall call)
    {
        if (await store.AllianceAsync(await Id(call),call.Cancellation) is not { } alliance) return PyNone.Instance;
        var counts = await store.AllianceCharacterCountsAsync([alliance.Id],call.Cancellation);
        var prime = await store.AlliancePrimeTimeAsync(alliance.Id,call.Cancellation);
        var sovereignty = readSovSnapshot is null ? null : await readSovSnapshot(alliance.Id,call.Cancellation);
        var soleSystemId = sovereignty is null ? null : SoleSovereigntySystemId(sovereignty,alliance.Id);
        var capital = await store.AllianceCapitalSystemAsync(alliance.Id,soleSystemId,call.Cancellation);
        var fields = AllianceFields(
            alliance,
            counts.GetValueOrDefault(alliance.Id),
            prime.CurrentHour,
            prime.NewHour,
            prime.ValidAfterFileTime,
            capital.CurrentSystemId);
        return Py.KeyVal([
            .. fields,
            ("__header__",Py.Tuple([.. AllianceHeader.Select(name => (PyValue)new PyStr(name))])),
            ("currentCapitalSystem",capital.CurrentSystemId is { } current ? new PyLong(current) : PyNone.Instance),
            ("newCapitalSystem",capital.NewSystemId is { } pending ? new PyLong(pending) : PyNone.Instance),
            ("newCapitalSystemValidAfter",new PyLong(capital.ValidAfterFileTime))
        ]);
    }
    [Method("GetAlliancePublicInfo")]
    public ValueTask<PyValue> GetAlliancePublicInfo(ServiceCall call) => GetAlliance(call);

    [Method("GetRankedAlliances")]
    public async ValueTask<PyValue> GetRankedAlliances(ServiceCall call)
    {
        var maxLength = call.Arg(0) is PyNone ? 100 : Math.Max(0,Number(call.Arg(0)));
        var alliances = await store.RankedAlliancesAsync(maxLength == 0 ? null : maxLength,call.Cancellation);
        var counts = await store.AllianceCharacterCountsAsync(alliances.Select(alliance => alliance.Id).ToArray(),call.Cancellation);
        return new PyList([.. alliances.Select(alliance => AllianceRow(alliance,counts.GetValueOrDefault(alliance.Id)))]);
    }

    private static readonly string[] MemberHeader = ["corporationID","allianceID","chosenExecutorID","startDate","deleted"];
    private static readonly Descriptor MemberDescriptor = CorporationWire.DescriptorOf(
        ("corporationID",3),("allianceID",3),("chosenExecutorID",3),("startDate",64),("deleted",11));
    private static PyValue[] Line(AllianceMember m,long? viewer) => [new PyInt(m.CorporationId),new PyInt(m.AllianceId),
        m.CorporationId == viewer ? CorporationWire.Id(m.ChosenExecutorId) : PyNone.Instance,CorporationWire.Time(m.StartedMs),new PyInt(0)];
    [Method("GetAllianceMembers")]
    public async ValueTask<PyValue> GetAllianceMembers(ServiceCall call)
    {
        return Py.Rowset(MemberHeader,(await store.AllianceMembersAsync(await Id(call),call.Cancellation)).Select(m => (PyValue)new PyList(Line(m,null))));
    }

    [Method("GetMembers")]
    public async ValueTask<PyValue> GetMembers(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } id || await characters.GetAsync(id,call.Cancellation) is not { AllianceId:not null } c)
            throw MachoErrors.ThrowUserError("CustomInfo",("info",new PyStr("Alliance membership required.")));
        var members = await store.AllianceMembersAsync(c.AllianceId.Value,call.Cancellation);
        return new PyObjectEx(ObjectExKind.Ex2,Py.Tuple(Py.Tuple(new PyToken("carbon.common.script.sys.crowset.CIndexedRowset")),
            Py.Dict(("header",MemberDescriptor.Header),("idName",new PyStr("corporationID")),("columnName",new PyStr("corporationID")))),[],
            [.. members.Select(m => new KeyValuePair<PyValue,PyValue>(new PyInt(m.CorporationId),new PyPackedRow(MemberDescriptor,Line(m,c.CorporationId))))]);
    }

    [Method("GetEmploymentRecord")]
    public async ValueTask<PyValue> GetEmploymentRecord(ServiceCall call)
    {
        var requested = Number(call.Arg(0));
        if (requested > 0 && await store.GetAsync(requested,call.Cancellation) is { } corporation)
        {
            var history = (await store.CorporationAllianceHistoryAsync(requested,call.Cancellation))
                .Select(row => ((long?)row.AllianceId,row.StartedMs,row.Deleted)).ToList();
            if (corporation.AllianceId is { } current && history.All(row => row.Item1 != current))
            {
                var member = (await store.AllianceMembersAsync(current,call.Cancellation))
                    .FirstOrDefault(row => row.CorporationId == corporation.Id);
                if (member is not null)
                {
                    var currentAlliance = await store.AllianceAsync(current,call.Cancellation);
                    history.Add((current,member.StartedMs,currentAlliance?.Deleted ?? false));
                }
            }
            var rows = history.OrderByDescending(row => row.StartedMs).ThenByDescending(row => row.Item1 ?? 0)
                .Select(row => (PyValue)Py.List(CorporationWire.Id(row.Item1),CorporationWire.Time(row.StartedMs),new PyInt(row.Deleted ? 1 : 0))).ToList();
            rows.Add(Py.List(PyNone.Instance,CorporationWire.Time(corporation.CreatedMs),new PyInt(corporation.Deleted ? 1 : 0)));
            return Py.Rowset(["allianceID","startDate","deleted"],rows);
        }

        var allianceRecord = await store.AllianceAsync(await Id(call),call.Cancellation);
        var allianceRows = allianceRecord is { } alliance
            ? new PyValue[] { Py.List(new PyInt(alliance.Id),CorporationWire.Time(alliance.CreatedMs),new PyInt(alliance.Deleted ? 1 : 0)) }
            : [];
        return Py.Rowset(["allianceID","startDate","deleted"],allianceRows);
    }

    [Method("GetDaysInAlliance")]
    public async ValueTask<PyValue> GetDaysInAlliance(ServiceCall call)
    {
        var alliance = await Id(call);
        var corporationId = Number(call.Arg(1));
        if (alliance <= 0 || corporationId <= 0 || await store.GetAsync(corporationId,call.Cancellation) is not { } corporation || corporation.AllianceId != alliance)
            return new PyInt(0);
        var member = (await store.AllianceMembersAsync(alliance,call.Cancellation)).FirstOrDefault(row => row.CorporationId == corporationId);
        return new PyInt(member is null ? 0 : AllianceMembershipDays(member.StartedMs));
    }

    [Method("GetAllianceMembersOlderThan")]
    public async ValueTask<PyValue> GetAllianceMembersOlderThan(ServiceCall call)
    {
        var alliance = await Id(call);
        var minimumDays = Math.Max(0,Number(call.Arg(1)));
        var members = await store.AllianceMembersAsync(alliance,call.Cancellation);
        return new PyList([.. members.Where(member => AllianceMembershipDays(member.StartedMs) >= minimumDays)
            .Select(member => (PyValue)new PyInt(member.CorporationId))]);
    }

    private static readonly Descriptor ApplicationDescriptor = CorporationWire.DescriptorOf(
        ("allianceID",3),("corporationID",3),("applicationText",130),("state",3),("applicationDateTime",64));

    private static Exception AllianceAccessDenied() => MachoErrors.ThrowUserError("CrpAccessDenied",
        ("reason",Py.List(new PyInt(101),new PyStr("UI/Corporations/AccessRestrictions/InsufficientRoles"))));

    private async Task<(long Actor,long Alliance)> RequireAllianceExecutorDirector(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor) throw AllianceAccessDenied();
        if (await characters.GetAsync(actor,call.Cancellation) is not { } character) throw AllianceAccessDenied();
        var corporation = await store.GetAsync(character.CorporationId,call.Cancellation);
        if (corporation is null || corporation.IsNpc || corporation.AllianceId is not { } allianceId)
            throw AllianceAccessDenied();

        var alliance = await store.AllianceAsync(allianceId,call.Cancellation);
        if (alliance is not { Deleted:false } || alliance.ActiveExecutorId != corporation.Id)
            throw AllianceAccessDenied();

        var member = await store.MemberAsync(actor,call.Cancellation);
        if (member is null || member.CorporationId != corporation.Id ||
            (CorporationRoles.Session(corporation,member,null,
                await store.TitlesAsync(corporation.Id,call.Cancellation)).AtAll & CorporationRoles.Director) == 0)
            throw AllianceAccessDenied();
        return (actor,allianceId);
    }

    [Method("UpdateAlliance")]
    public async ValueTask<PyValue> UpdateAlliance(ServiceCall call)
    {
        var (actor,alliance) = await RequireAllianceExecutorDirector(call);
        var beforeAlliance = await AllianceNotificationFieldsAsync(alliance,call.Cancellation);
        var description = JsSemantics.StringValue(call.Arg(0)) ?? "";
        var url = JsSemantics.StringValue(call.Arg(1)) ?? "";
        try
        {
            await store.UpdateAllianceAsync(actor,alliance,description,url,call.Cancellation);
        }
        catch (CorporationError error) when (error.Code is "CHARACTER_NOT_FOUND" or "CORPORATION_NOT_FOUND" or
            "CORPORATION_ACCESS_DENIED" or "ALLIANCE_EXECUTOR_REQUIRED" or "ALLIANCE_PUBLIC_INFO_INVALID_LENGTH")
        {
            throw AllianceAccessDenied();
        }
        var afterAlliance = await AllianceNotificationFieldsAsync(alliance,call.Cancellation);
        CorporationNotificationAdapter.AllianceChanged(characterSessions,call,alliance,beforeAlliance,afterAlliance);
        return PyNone.Instance;
    }


    [Method("SetPrimeHour")]
    public async ValueTask<PyValue> SetPrimeHour(ServiceCall call)
    {
        var (actor,alliance) = await RequireAllianceExecutorDirector(call);
        var beforeAlliance = await AllianceNotificationFieldsAsync(alliance,call.Cancellation);
        var hour = call.Arg(0) is PyInt or PyLong ? Number(call.Arg(0)) : -1;
        if (hour is < 0 or > 23) throw AllianceAccessDenied();

        var sovereignty = await ReadAllianceSovSnapshotAsync(alliance, call.Cancellation);
        var effectiveAfter = sovereignty.NextVulnerabilityWindowEndFileTime;
        if (effectiveAfter is not > 0 || effectiveAfter <= store.VoteNowFiletime)
            throw new CorporationError("ALLIANCE_PRIME_TIME_WINDOW_UNAVAILABLE");
        try
        {
            await store.SetAlliancePrimeHourAsync(actor,alliance,(int)hour,effectiveAfter.Value,call.Cancellation);
        }
        catch (CorporationError error) when (error.Code is "CHARACTER_NOT_FOUND" or "CORPORATION_NOT_FOUND" or
            "CORPORATION_ACCESS_DENIED" or "ALLIANCE_EXECUTOR_REQUIRED" or "ALLIANCE_NOT_FOUND" or "ALLIANCE_PRIME_HOUR_INVALID")
        {
            throw AllianceAccessDenied();
        }
        var afterAlliance = await AllianceNotificationFieldsAsync(alliance,call.Cancellation);
        CorporationNotificationAdapter.AllianceChanged(characterSessions,call,alliance,beforeAlliance,afterAlliance);
        return PyNone.Instance;
    }

[Method("GetPrimeTimeInfo")]
    public async ValueTask<PyValue> GetPrimeTimeInfo(ServiceCall call)
    {
        var (_, alliance) = await RequireAllianceMember(call);
        var state = await store.AlliancePrimeTimeAsync(alliance, call.Cancellation);
        return Py.KeyVal([
            ("currentPrimeHour", new PyInt(state.CurrentHour)),
            ("newPrimeHour", new PyInt(state.NewHour)),
            ("newPrimeHourValidAfter", new PyLong(state.ValidAfterFileTime))
        ]);
    }

    [Method("GetCapitalSystemInfo")]
    public async ValueTask<PyValue> GetCapitalSystemInfo(ServiceCall call)
    {
        var (_, alliance) = await RequireAllianceMember(call);
        var sovereignty = await ReadAllianceSovSnapshotAsync(alliance, call.Cancellation);
        var soleSystemId = SoleSovereigntySystemId(sovereignty, alliance);
        var state = await store.AllianceCapitalSystemAsync(alliance, soleSystemId, call.Cancellation);
        return Py.KeyVal([
            ("currentCapitalSystem", state.CurrentSystemId is { } current ? new PyLong(current) : PyNone.Instance),
            ("newCapitalSystem", state.NewSystemId is { } pending ? new PyLong(pending) : PyNone.Instance),
            ("newCapitalSystemValidAfter", new PyLong(state.ValidAfterFileTime))
        ]);
    }

    [Method("SetCapitalSystem")]
    public async ValueTask<PyValue> SetCapitalSystem(ServiceCall call)
    {
        var (actor, alliance) = await RequireAllianceExecutorDirector(call);
        var beforeAlliance = await AllianceNotificationFieldsAsync(alliance,call.Cancellation);
        var solarSystemId = call.Arg(0) is PyInt or PyLong ? Number(call.Arg(0)) : 0;
        if (solarSystemId <= 0) throw new CorporationError("ALLIANCE_CAPITAL_SYSTEM_INVALID");

        var sovereignty = await ReadAllianceSovSnapshotAsync(alliance, call.Cancellation);
        var soleSystemId = SoleSovereigntySystemId(sovereignty, alliance);
        if (!sovereignty.HubsBySolarSystemId.TryGetValue(solarSystemId, out var hub) ||
            hub.AllianceId != alliance || hub.HubId is not > 0)
            throw new CorporationError("ALLIANCE_CAPITAL_SYSTEM_NOT_SOV_HUB");
        if (soleSystemId is { } sole && sole != solarSystemId)
            throw new CorporationError("ALLIANCE_CAPITAL_SYSTEM_NOT_SOV_HUB");

        await store.SetAllianceCapitalSystemAsync(actor, alliance, solarSystemId, soleSystemId, call.Cancellation);
        var afterAlliance = await AllianceNotificationFieldsAsync(alliance,call.Cancellation);
        CorporationNotificationAdapter.AllianceChanged(characterSessions,call,alliance,beforeAlliance,afterAlliance);
        return PyNone.Instance;
    }

    [Method("CancelCapitalSystemTransition")]
    public async ValueTask<PyValue> CancelCapitalSystemTransition(ServiceCall call)
    {
        var (actor, alliance) = await RequireAllianceExecutorDirector(call);
        var beforeAlliance = await AllianceNotificationFieldsAsync(alliance,call.Cancellation);
        var sovereignty = await ReadAllianceSovSnapshotAsync(alliance, call.Cancellation);
        await store.CancelAllianceCapitalSystemTransitionAsync(
            actor, alliance, SoleSovereigntySystemId(sovereignty, alliance), call.Cancellation);
        var afterAlliance = await AllianceNotificationFieldsAsync(alliance,call.Cancellation);
        CorporationNotificationAdapter.AllianceChanged(characterSessions,call,alliance,beforeAlliance,afterAlliance);
        return PyNone.Instance;
    }

    private async Task<AllianceSovSnapshot> ReadAllianceSovSnapshotAsync(long alliance, CancellationToken cancellationToken) =>
        readSovSnapshot is not null && await readSovSnapshot(alliance, cancellationToken) is { } snapshot
            ? snapshot
            : throw new CorporationError("ALLIANCE_SOV_SNAPSHOT_UNAVAILABLE");

    private static long? SoleSovereigntySystemId(AllianceSovSnapshot snapshot, long alliance)
    {
        var systems = snapshot.ClaimAllianceIdsBySolarSystemId
            .Where(system => system.Value == alliance)
            .Select(system => system.Key)
            .Take(2)
            .ToArray();
        return systems.Length == 1 ? systems[0] : null;
    }

    [Method("GetApplications")]
    public async ValueTask<PyValue> GetApplications(ServiceCall call)
    {
        var (_,allianceId) = await RequireAllianceExecutorDirector(call);
        var applications = await store.AllianceApplicationsAsync(allianceId,call.Cancellation);
        return new PyObjectEx(ObjectExKind.Ex2,
            Py.Tuple(Py.Tuple(new PyToken("carbon.common.script.sys.crowset.CIndexedRowset")),
                Py.Dict(("header",ApplicationDescriptor.Header),("idName",new PyStr("corporationID")),
                    ("columnName",new PyStr("corporationID")))),[],
            [.. applications.Select(application => new KeyValuePair<PyValue,PyValue>(
                new PyInt(application.CorporationId),
                new PyPackedRow(ApplicationDescriptor,[
                    new PyInt(application.AllianceId),new PyInt(application.CorporationId),new PyWString(application.Text),
                    new PyInt(application.Status),CorporationWire.Time(application.CreatedMs)])))]);
    }

    [Method("UpdateApplication")]
    public async ValueTask<PyValue> UpdateApplication(ServiceCall call)
    {
        var (actor, allianceId) = await RequireAllianceExecutorDirector(call);
        var corporationId = Number(call.Arg(0));
        var applicationText = JsSemantics.StringValue(call.Arg(1)) ?? "";
        var state = Number(call.Arg(2));
        if (corporationId <= 0) throw new CorporationError("ALLIANCE_APPLICATION_NOT_FOUND");
        if (state is not (2 or 4)) throw new CorporationError("ALLIANCE_APPLICATION_INVALID_STATE");

        var application = (await store.AllianceApplicationsAsync(allianceId, call.Cancellation))
            .LastOrDefault(item => item.CorporationId == corporationId);
        if (application is null) throw new CorporationError("ALLIANCE_APPLICATION_NOT_FOUND");

        AllianceApplication updated;
        if (state == 2)
        {
            if (scheduleAllianceApplication is null) throw new CorporationError("ALLIANCE_DUE_SCHEDULER_UNAVAILABLE");
            updated = await store.RespondToAllianceAsync(actor, application.Id, applicationText, (int)state,
                scheduleAllianceApplication, call.Cancellation);
            wakeAllianceApplicationScheduler?.Invoke();
        }
        else
        {
            updated = await store.RespondToAllianceAsync(actor, application.Id, applicationText, (int)state,
                call.Cancellation);
        }
        var recipients = await CorporationNotificationAdapter.AllianceApplicationRecipientCharacterIdsAsync(
            store,allianceId,corporationId,call.Cancellation);
        CorporationNotificationAdapter.AllianceApplicationChanged(
            characterSessions,call,allianceId,corporationId,application,updated,recipients);
        return PyNone.Instance;
    }

    [Method("DeclareExecutorSupport")]
    public async ValueTask<PyValue> DeclareExecutorSupport(ServiceCall call)
    {
        var (actor, allianceId) = await RequireAllianceDirector(call);
        var corporationId = (await characters.GetAsync(actor, call.Cancellation))?.CorporationId
            ?? throw AllianceAccessDenied();
        var beforeMember = (await store.AllianceMembersAsync(allianceId, call.Cancellation))
            .SingleOrDefault(member => member.CorporationId == corporationId);
        var beforeAlliance = await AllianceNotificationFieldsAsync(allianceId, call.Cancellation);

        await store.ChooseExecutorAsync(actor, Number(call.Arg(0)), call.Cancellation);

        var afterMember = (await store.AllianceMembersAsync(allianceId, call.Cancellation))
            .SingleOrDefault(member => member.CorporationId == corporationId);
        var afterAlliance = await AllianceNotificationFieldsAsync(allianceId, call.Cancellation);
        CorporationNotificationAdapter.AllianceMemberChanged(
            characterSessions, call, allianceId, corporationId, beforeMember, afterMember);
        CorporationNotificationAdapter.AllianceChanged(
            characterSessions, call, allianceId, beforeAlliance, afterAlliance);
        return PyNone.Instance;
    }


    [Method("DeleteMember")]
    public async ValueTask<PyValue> DeleteMember(ServiceCall call)
    {
        var actor = call.Session.CharacterId ?? throw AllianceAccessDenied();
        var character = await characters.GetAsync(actor, call.Cancellation) ?? throw AllianceAccessDenied();
        var actorCorporation = await store.GetAsync(character.CorporationId, call.Cancellation)
            ?? throw AllianceAccessDenied();
        var corporationId = Number(call.Arg(0));
        if (corporationId <= 0) throw AllianceAccessDenied();
        var mode = corporationId == actorCorporation.Id ? "self-leave" : "executor-kick";
        var allianceId = call.BoundObjectId is { } oid && _alliancesByObject.TryGetValue(oid, out var bound)
            ? bound
            : actorCorporation.AllianceId ?? call.Session.GetInt("allianceid") ?? 0;

        if (allianceId <= 0) throw AllianceAccessDenied();
        var target = await store.GetAsync(corporationId, call.Cancellation);
        if (target?.AllianceId != allianceId)
        {
            if (await store.HasAllianceDepartureReceiptAsync(allianceId, corporationId, actor, mode, call.Cancellation))
                return PyNone.Instance;
            throw AllianceAccessDenied();
        }

        var authority = mode == "self-leave"
            ? await RequireAllianceDirector(call)
            : await RequireAllianceExecutorDirector(call);
        if (authority.Alliance != allianceId) throw AllianceAccessDenied();
        var beforeMember = (await store.AllianceMembersAsync(allianceId, call.Cancellation))
            .SingleOrDefault(member => member.CorporationId == corporationId);
        if (beforeMember is null) throw AllianceAccessDenied();
        var beforeAlliance = await AllianceNotificationFieldsAsync(allianceId, call.Cancellation);
        var result = await store.RemoveAllianceMemberAsync(allianceId, corporationId, actor, mode, now(),
            WriteAllianceAffiliationAsync, call.Cancellation);
        if (result.Duplicate) return PyNone.Instance;
        var afterAlliance = await AllianceNotificationFieldsAsync(allianceId, call.Cancellation);
        CorporationNotificationAdapter.AllianceMemberChanged(
            characterSessions, call, allianceId, corporationId, result.Member, null);
        CorporationNotificationAdapter.AllianceChanged(
            characterSessions, call, allianceId, beforeAlliance, afterAlliance);
        CorporationNotificationAdapter.CorporationChanged(
            characterSessions, call, corporationId, "allianceID",
            new PyInt(allianceId), PyNone.Instance);
        return PyNone.Instance;
    }

    private async Task WriteAllianceAffiliationAsync(System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,long corporationId,long? allianceId,CancellationToken token)
    {
        if (relations is null) throw new CorporationError("ALLIANCE_RELATIONS_UNAVAILABLE");
        var pgConnection = (Npgsql.NpgsqlConnection)connection;
        var pgTransaction = (Npgsql.NpgsqlTransaction)transaction;
        var kind = Eve.Relations.RelationKinds.AllianceMember.Name;
        if (allianceId is > 0)
        {
            await relations.SetAsync(pgConnection,pgTransaction,kind,corporationId,allianceId.Value,1,
                "alliance membership",cancellationToken:token);
            return;
        }
        foreach (var current in await relations.FromAsync(kind,corporationId,pgConnection,pgTransaction,token))
            await relations.EndAsync(pgConnection,pgTransaction,kind,corporationId,current.Object,
                "alliance membership ended",token);
    }


    [Method("GetBills")]
    public async ValueTask<PyValue> GetBills(ServiceCall call)
    {
        var authority = await RequireAllianceRole(call,
            CorporationRoles.Accountant | CorporationRoles.JuniorAccountant,false);
        return Py.List((await store.AllianceBillsAsync(authority.Alliance,call.Cancellation))
            .Select(AllianceBill).ToArray());
    }

    [Method("GetBillsReceivable")]
    public async ValueTask<PyValue> GetBillsReceivable(ServiceCall call)
    {
        var authority = await RequireAllianceRole(call,
            CorporationRoles.Accountant | CorporationRoles.JuniorAccountant,false);
        return Py.List((await store.AllianceBillsReceivableAsync(authority.Alliance,call.Cancellation))
            .Select(AllianceBill).ToArray());
    }

    [Method("GetBillBalance")]
    public async ValueTask<PyValue> GetBillBalance(ServiceCall call)
    {
        var authority = await RequireAllianceRole(call,
            CorporationRoles.Accountant | CorporationRoles.JuniorAccountant,false);
        var accountKey = call.Session.GetInt("corpAccountKey") ?? call.Session.GetInt("corpaccountkey") ?? 1000;
        if (accountKey is < 1000 or > 1006) accountKey = 1000;
        var balance = await store.CorporationWalletBalanceAsync(authority.Corporation,(int)accountKey,call.Cancellation);
        return Py.Number((double)(balance ?? 0m));
    }

    [Method("PayBill")]
    public async ValueTask<PyValue> PayBill(ServiceCall call)
    {
        var authority = await RequireAllianceRole(call,CorporationRoles.Accountant,true);
        var billId = Number(call.Arg(0));
        var accountKey = Number(call.Kwarg("fromAccountKey") ?? call.Arg(1));
        if (accountKey is < 1000 or > 1006) accountKey = 1000;
        var paid = await store.PayAllianceBillAsync(billId,authority.Alliance,authority.Corporation,
            authority.Actor,(int)accountKey,now(),call.Cancellation);
        if (!paid) throw new CorporationError("BILL_NOT_PAYABLE");
        return PyNone.Instance;
    }

    [Method("DeclareWarAgainst")]
    public async ValueTask<PyValue> DeclareWarAgainst(ServiceCall call)
    {
        var (actor,allianceId) = await RequireAllianceExecutorDirector(call);
        if (relations is null) throw new CorporationError("WAR_DECLARATION_UNAVAILABLE");
        var againstId = Number(call.Arg(0));
        if (againstId <= 0 || againstId == allianceId)
            throw new CorporationError("ALLIANCE_WAR_DECLARATION_INVALID");
        var warHq = Number(call.Arg(1));
        var warHqId = warHq > 0 ? warHq : (long?)null;
        var declaredAt = Number(CorporationWire.Time(now()));
        var startedAt = checked(declaredAt + 864_000_000_000L);
        var dueAt = checked(declaredAt + 6_048_000_000_000L);
        var callId = Number(call.CallId);
        var operationKey = callId > 0
            ? $"alliance-war:{allianceId}:{actor}:{callId}"
            : Guid.NewGuid().ToString("N");
        var result = await store.CreateAllianceWarDeclarationAsync(
            allianceId,actor,againstId,warHqId,declaredAt,startedAt,dueAt,4,100_000_000m,1_000_125,
            operationKey,
            async (connection,transaction,billId,token) =>
            {
                if (await relations.GetAsync("war",allianceId,againstId,connection,transaction,token,forUpdate:true) is not null)
                    throw new CorporationError("WAR_ALREADY_DECLARED");
                var attributes = new
                {
                    declaredByCharacterID=actor,
                    timeDeclared=declaredAt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    timeStarted=startedAt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    timeFinished=(string?)null,
                    retracted=(string?)null,
                    retractedBy=(long?)null,
                    billID=billId,
                    mutual=0,
                    fightingLegalNotified=0,
                    openForAllies=0,
                    createdFromWarID=(long?)null,
                    warHQID=warHqId,
                    warHQ=warHqId,
                    reasonEnded=0,
                    reasonStarted=0,
                    reward=0
                };
                var change = await relations.SetAsync(connection,transaction,"war",allianceId,againstId,1.0,
                    "declared",attributes,cancellationToken:token);
                if (change is not null) return change.RelationId;
                return (await relations.GetAsync("war",allianceId,againstId,connection,transaction,token,forUpdate:true))?.Id ?? 0;
            },call.Cancellation);
        var warRecord = Py.KeyVal(
            ("warID",CorporationWire.Id(result.WarId)),("declaredByID",CorporationWire.Id(allianceId)),
            ("againstID",CorporationWire.Id(againstId)),
            ("warHQID",CorporationWire.Id(warHqId)),("timeDeclared",new PyLong(declaredAt)),
            ("timeStarted",new PyLong(startedAt)),("timeFinished",PyNone.Instance),("retracted",PyNone.Instance),
            ("retractedBy",PyNone.Instance),("billID",CorporationWire.Id(result.BillId)),
            ("mutual",new PyInt(0)),("fightingLegalNotified",new PyInt(0)),("openForAllies",new PyInt(0)),
            ("allies",new PyDict([])),("createdFromWarID",PyNone.Instance),("reward",Py.Number(0)));
        if (!result.Replayed) CorporationNotificationAdapter.WarChanged(characterSessions,call,allianceId,againstId,warRecord);
        return warRecord;
    }

    private static PyValue AllianceBill(CorporationBill bill) => Py.KeyVal(
        ("billID",Py.Number(bill.BillId)),
        ("billTypeID",new PyInt(bill.BillTypeId)),
        ("amount",Py.Number((double)bill.AmountIsk)),
        ("interest",Py.Number((double)bill.Interest)),
        ("debtorID",Py.Number(bill.DebtorId)),
        ("creditorID",Py.Number(bill.CreditorId)),
        ("dueDateTime",new PyLong(bill.DueFiletime)),
        ("paid",new PyInt(bill.Paid ? 1 : 0)),
        ("paidDateTime",bill.PaidFiletime is { } paidAt ? new PyLong(paidAt) : PyNone.Instance),
        ("paidByOwnerID",Py.Number(bill.PaidByOwnerId ?? 0)),
        ("externalID",Py.Number(bill.ExternalId)),
        ("externalID2",Py.Number(bill.ExternalId2)));


    [Method("GMForceAcceptApplication")]
    public async ValueTask<PyValue> GMForceAcceptApplication(ServiceCall call)
    {
        const long gmlRole = 1L << 54;
        if (((call.Session.GetInt("role") ?? 0) & gmlRole) == 0)
            throw AllianceAccessDenied();
        var allianceId = call.BoundObjectId is { } oid && _alliancesByObject.TryGetValue(oid,out var bound)
            ? bound
            : call.Session.GetInt("allianceid") ?? 0;
        if (allianceId <= 0 && call.Session.CharacterId is { } characterId)
            allianceId = (await characters.GetAsync(characterId,call.Cancellation))?.AllianceId ?? 0;
        if (allianceId <= 0) throw new CorporationError("ALLIANCE_NOT_FOUND");
        RequireBoundAlliance(call,allianceId);
        if (scheduleAllianceApplication is null)
            throw new CorporationError("ALLIANCE_DUE_SCHEDULER_UNAVAILABLE");
        var corporationId = Number(call.Arg(0));
        var application = (await store.AllianceApplicationsAsync(allianceId,call.Cancellation))
            .LastOrDefault(item => item.CorporationId == corporationId);
        if (application is null) throw new CorporationError("ALLIANCE_APPLICATION_NOT_FOUND");
        var updated = await store.ForceAcceptAllianceApplicationAsync(
            application.Id,scheduleAllianceApplication,call.Cancellation);
        wakeAllianceApplicationScheduler?.Invoke();
        var recipients = await CorporationNotificationAdapter.AllianceApplicationRecipientCharacterIdsAsync(
            store,allianceId,corporationId,call.Cancellation);
        CorporationNotificationAdapter.AllianceApplicationChanged(
            characterSessions,call,allianceId,corporationId,application,updated,recipients);
        return PyNone.Instance;
    }

    [Method("GetRelationships")]
    public async ValueTask<PyValue> GetRelationships(ServiceCall call)
    {
        var (_,alliance) = await RequireAllianceMember(call);
        var relationStore = relations ?? throw new InvalidOperationException("Alliance relationship RPCs require RelationStore registration.");
        var rows = await relationStore.FromAsync(Eve.Relations.RelationKinds.Standing.Name,alliance,cancellationToken:call.Cancellation);
        var descriptor = CorporationWire.DescriptorOf(("toID",3),("relationship",3));
        return new PyObjectEx(ObjectExKind.Ex2,
            Py.Tuple(Py.Tuple(new PyToken("carbon.common.script.sys.crowset.CIndexedRowset")),
                Py.Dict(("header",descriptor.Header),("idName",new PyStr("toID")),("columnName",new PyStr("toID")))),
            [],[.. rows.Select(row => new KeyValuePair<PyValue,PyValue>(new PyInt(row.Object),
                new PyPackedRow(descriptor,[new PyInt(row.Object),new PyInt((long)row.Value)])))]);
    }

    [Method("SetRelationship")]
    public async ValueTask<PyValue> SetRelationship(ServiceCall call)
    {
        var (actor,alliance) = await RequireAllianceExecutorDirector(call);
        RequireBoundAlliance(call,alliance);
        var relationship = Number(call.Arg(0));
        var toId = Number(call.Arg(1));
        if (toId <= 0 || toId == alliance) throw AllianceAccessDenied();
        var relationStore = relations ?? throw new InvalidOperationException("Alliance relationship RPCs require RelationStore registration.");
        var kind = Eve.Relations.RelationKinds.Standing.Name;
        var before = await relationStore.GetAsync(kind,alliance,toId,cancellationToken:call.Cancellation);
        await store.WriteAllianceRelationshipAsync(actor,alliance,async (connection,transaction) =>
        {
            await relationStore.SetAsync(connection,transaction,kind,
                alliance,toId,relationship,"alliance relationship",cancellationToken:call.Cancellation);
        },call.Cancellation);
        var after = await relationStore.GetAsync(kind,alliance,toId,cancellationToken:call.Cancellation);
        var recipients = (await store.AllianceMembersAsync(alliance,call.Cancellation))
            .Select(member => member.CorporationId).ToArray();
        CorporationNotificationAdapter.AllianceRelationshipChanged(
            characterSessions,call,alliance,toId,before?.Value,after?.Value,recipients);
        return PyNone.Instance;
    }

    [Method("DeleteRelationship")]
    public async ValueTask<PyValue> DeleteRelationship(ServiceCall call)
    {
        var (actor,alliance) = await RequireAllianceExecutorDirector(call);
        RequireBoundAlliance(call,alliance);
        var toId = Number(call.Arg(0));
        if (toId <= 0 || toId == alliance) throw AllianceAccessDenied();
        var relationStore = relations ?? throw new InvalidOperationException("Alliance relationship RPCs require RelationStore registration.");
        var kind = Eve.Relations.RelationKinds.Standing.Name;
        var before = await relationStore.GetAsync(kind,alliance,toId,cancellationToken:call.Cancellation);
        await store.WriteAllianceRelationshipAsync(actor,alliance,async (connection,transaction) =>
        {
            await relationStore.EndAsync(connection,transaction,kind,
                alliance,toId,"alliance relationship deleted",call.Cancellation);
        },call.Cancellation);
        var after = await relationStore.GetAsync(kind,alliance,toId,cancellationToken:call.Cancellation);
        var recipients = (await store.AllianceMembersAsync(alliance,call.Cancellation))
            .Select(member => member.CorporationId).ToArray();
        CorporationNotificationAdapter.AllianceRelationshipChanged(
            characterSessions,call,alliance,toId,before?.Value,after?.Value,recipients);
        return PyNone.Instance;
    }

    [Method("GetContactList")]
    public ValueTask<PyValue> GetContactList(ServiceCall call) => GetAllianceContacts(call);

    [Method("GetAllianceContacts")]
    public async ValueTask<PyValue> GetAllianceContacts(ServiceCall call)
    {
        var (_,alliance) = await RequireAllianceMember(call);
        var contacts = await store.AllianceContactsAsync(alliance,call.Cancellation);
        var addresses = contacts.Select(contact => (PyValue)new PyList([
            new PyInt(contact.ContactId),new PyInt(0),new PyInt(contact.RelationshipId),new PyInt(contact.LabelMask)
        ])).ToArray();
        return Py.KeyVal(
            ("addresses",Py.Rowset(["contactID","inWatchlist","relationshipID","labelMask"],addresses)),
            ("blocked",Py.Rowset(["senderID"],Array.Empty<PyValue>())));
    }

    [Method("AddAllianceContact")]
    public async ValueTask<PyValue> AddAllianceContact(ServiceCall call)
    {
        var (actor,alliance) = await RequireAllianceExecutorDirector(call);
        RequireBoundAlliance(call,alliance);
        await store.AddAllianceContactAsync(actor,alliance,Number(call.Arg(0)),Number(call.Arg(1)),call.Cancellation);
        return PyNone.Instance;
    }

    [Method("EditAllianceContact")]
    public async ValueTask<PyValue> EditAllianceContact(ServiceCall call)
    {
        var (actor,alliance) = await RequireAllianceExecutorDirector(call);
        RequireBoundAlliance(call,alliance);
        await store.EditAllianceContactAsync(actor,alliance,Number(call.Arg(0)),Number(call.Arg(1)),call.Cancellation);
        return PyNone.Instance;
    }

    [Method("RemoveAllianceContacts")]
    public async ValueTask<PyValue> RemoveAllianceContacts(ServiceCall call)
    {
        var (actor,alliance) = await RequireAllianceExecutorDirector(call);
        RequireBoundAlliance(call,alliance);
        await store.RemoveAllianceContactsAsync(actor,alliance,ContactIds(call.Arg(0)),call.Cancellation);
        return PyNone.Instance;
    }

    [Method("EditContactsRelationshipID")]
    public async ValueTask<PyValue> EditContactsRelationshipID(ServiceCall call)
    {
        var (actor,alliance) = await RequireAllianceExecutorDirector(call);
        RequireBoundAlliance(call,alliance);
        await store.EditAllianceContactsRelationshipAsync(actor,alliance,ContactIds(call.Arg(0)),Number(call.Arg(1)),call.Cancellation);
        return PyNone.Instance;
    }

    [Method("GetLabels")]
    public async ValueTask<PyValue> GetLabels(ServiceCall call)
    {
        var (_,alliance) = await RequireAllianceMember(call);
        var labels = await store.AllianceContactLabelsAsync(alliance,call.Cancellation);
        return new PyDict(labels.Select(label => new KeyValuePair<PyValue,PyValue>(new PyInt(label.LabelId),
            Py.KeyVal(("labelID",new PyInt(label.LabelId)),("name",new PyStr(label.Name)),("color",new PyInt(label.Color))))).ToArray());
    }

    [Method("CreateLabel")]
    public async ValueTask<PyValue> CreateLabel(ServiceCall call)
    {
        var (actor,alliance) = await RequireAllianceExecutorDirector(call);
        RequireBoundAlliance(call,alliance);
        var name = JsSemantics.StringValue(call.Arg(0)) ?? "";
        var color = Number(call.Arg(1));
        if (color is < 0 or > 16777215) throw AllianceAccessDenied();
        var label = await store.CreateAllianceContactLabelAsync(actor,alliance,name,(int)color,call.Cancellation);
        return new PyInt(label.LabelId);
    }

    [Method("DeleteLabel")]
    public async ValueTask<PyValue> DeleteLabel(ServiceCall call)
    {
        var (actor,alliance) = await RequireAllianceExecutorDirector(call);
        RequireBoundAlliance(call,alliance);
        await store.DeleteAllianceContactLabelAsync(actor,alliance,Number(call.Arg(0)),call.Cancellation);
        return PyNone.Instance;
    }

    [Method("EditLabel")]
    public async ValueTask<PyValue> EditLabel(ServiceCall call)
    {
        var (actor,alliance) = await RequireAllianceExecutorDirector(call);
        RequireBoundAlliance(call,alliance);
        var name = call.Arg(1) is PyNone ? null : JsSemantics.StringValue(call.Arg(1));
        int? color = null;
        if (call.Arg(2) is not PyNone)
        {
            var value = Number(call.Arg(2));
            if (value is < 0 or > 16777215) throw AllianceAccessDenied();
            color = (int)value;
        }
        await store.EditAllianceContactLabelAsync(actor,alliance,Number(call.Arg(0)),name,color,call.Cancellation);
        return PyNone.Instance;
    }

    [Method("AssignLabels")]
    public async ValueTask<PyValue> AssignLabels(ServiceCall call)
    {
        var (actor,alliance) = await RequireAllianceExecutorDirector(call);
        RequireBoundAlliance(call,alliance);
        var mask = Number(call.Arg(1));
        if (mask < 0) throw AllianceAccessDenied();
        await store.AssignAllianceContactLabelsAsync(actor,alliance,ContactIds(call.Arg(0)),mask,call.Cancellation);
        return PyNone.Instance;
    }

    [Method("RemoveLabels")]
    public async ValueTask<PyValue> RemoveLabels(ServiceCall call)
    {
        var (actor,alliance) = await RequireAllianceExecutorDirector(call);
        RequireBoundAlliance(call,alliance);
        var mask = Number(call.Arg(1));
        if (mask < 0) throw AllianceAccessDenied();
        await store.RemoveAllianceContactLabelsAsync(actor,alliance,ContactIds(call.Arg(0)),mask,call.Cancellation);
        return PyNone.Instance;
    }

    private async Task<(long Actor,long Alliance)> RequireAllianceMember(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor ||
            await characters.GetAsync(actor,call.Cancellation) is not { } character)
            throw AllianceAccessDenied();
        var corporation = await store.GetAsync(character.CorporationId,call.Cancellation);
        if (corporation is null || corporation.IsNpc || corporation.AllianceId is not { } alliance ||
            await store.AllianceAsync(alliance,call.Cancellation) is not { Deleted:false })
            throw AllianceAccessDenied();
        RequireBoundAlliance(call,alliance);
        return (actor,alliance);
    }

    private void RequireBoundAlliance(ServiceCall call,long alliance)
    {
        if (call.BoundObjectId is not { } oid) return;
        if (!_alliancesByObject.TryGetValue(oid,out var bound) || bound != alliance)
            throw AllianceAccessDenied();
    }

    private static long[] ContactIds(PyValue value)
    {
        var items = value switch
        {
            PyList list => list.Items,
            PyTuple tuple => tuple.Items,
            _ => throw AllianceAccessDenied(),
        };
        var ids = items.Select(Number).ToArray();
        if (ids.Any(id => id <= 0)) throw AllianceAccessDenied();
        return ids.Distinct().ToArray();
    }

    [Method("MachoResolveObject")]
    public async ValueTask<PyValue> MachoResolveObject(ServiceCall call) =>
        await store.AllianceAsync(await Id(call),call.Cancellation) is not null ? new PyInt(MachoMessage.ProxyNodeId) : PyNone.Instance;

    [Method("MachoBindObject")]
    public async ValueTask<PyValue> MachoBindObject(ServiceCall call)
    {
        var alliance = await Id(call);
        if (await store.AllianceAsync(alliance,call.Cancellation) is null) return PyNone.Instance;
        var oid = $"N={MachoMessage.ProxyNodeId}:{BoundObjectIds.Next()}";
        _alliancesByObject[oid] = alliance;
        PyValue result = PyNone.Instance;
        if (call.Arg(1) is PyTuple { Items.Count:>0 } nested)
        {
            var args = nested.Items.Count>1 ? nested.Items[1] switch { PyTuple t => t.Items,PyList l => l.Items,_ => [] } : [];
            result = await ((IService)this).CallAsync(new ServiceCall { Method=JsSemantics.StringValue(nested.Items[0]) ?? "",Args=args,
                Kwargs=nested.Items.Count>2 ? nested.Items[2] : PyNone.Instance,Session=call.Session,CallId=call.CallId,BoundObjectId=oid,Cancellation=call.Cancellation });
        }
        return Py.Tuple(new PySubStruct(new PySubStream(Py.Tuple(new PyStr(oid),CorporationWire.Time(now())))),result);
    }
    public void OnBoundObjectReleased(string oid,ClientSession? session) => _alliancesByObject.TryRemove(oid,out _);

    [Method("GetBulletins")]
    public async ValueTask<PyValue> GetBulletins(ServiceCall call)
    {
        var authority = await RequireAllianceMember(call);
        var bulletins = await store.AllianceBulletinsAsync(authority.Alliance, call.Cancellation);
        var descriptor = AllianceBulletinDescriptor;
        return new PyObject(new PyStr("carbon.common.script.sys.crowset.CRowset"), Py.Dict(
            ("header", descriptor.Header),
            ("RowClass", new PyToken("util.Row")),
            ("lines", new PyList([.. bulletins.Select(bulletin => AllianceBulletinRow(descriptor, bulletin))]))));
    }

    [Method("GetBulletinEntries")]
    public ValueTask<PyValue> GetBulletinEntries(ServiceCall call) => GetBulletins(call);

    [Method("GetBulletin")]
    public async ValueTask<PyValue> GetBulletin(ServiceCall call)
    {
        var authority = await RequireAllianceMember(call);
        var bulletin = await store.AllianceBulletinAsync(authority.Alliance, Number(call.Arg(0)), call.Cancellation);
        return bulletin is null ? PyNone.Instance : AllianceBulletinRow(AllianceBulletinDescriptor, bulletin);
    }

    [Method("AddBulletin")]
    public async ValueTask<PyValue> AddBulletin(ServiceCall call)
    {
        var (actor, alliance) = await RequireAllianceDirector(call);
        var nowFiletime = Number(CorporationWire.Time(now()));
        var editFiletime = Number(call.Arg(3));
        var bulletinId = Number(call.Arg(2));
        var id = await store.AddAllianceBulletinAsync(alliance, actor,
            JsSemantics.StringValue(call.Arg(0)) ?? "", JsSemantics.StringValue(call.Arg(1)) ?? "",
            bulletinId > 0 ? bulletinId : null, nowFiletime,
            editFiletime > 0 ? editFiletime : nowFiletime, call.Cancellation);
        return new PyInt(id);
    }

    [Method("UpdateBulletin")]
    public async ValueTask<PyValue> UpdateBulletin(ServiceCall call)
    {
        var (actor, alliance) = await RequireAllianceDirector(call);
        var bulletinId = Number(call.Arg(0));
        if (bulletinId <= 0) throw new CorporationError("ALLIANCE_BULLETIN_INVALID");
        var nowFiletime = Number(CorporationWire.Time(now()));
        var editFiletime = Number(call.Arg(3));
        return new PyInt(await store.AddAllianceBulletinAsync(alliance, actor,
            JsSemantics.StringValue(call.Arg(1)) ?? "", JsSemantics.StringValue(call.Arg(2)) ?? "",
            bulletinId, nowFiletime, editFiletime > 0 ? editFiletime : nowFiletime, call.Cancellation));
    }

    [Method("DeleteBulletin")]
    public async ValueTask<PyValue> DeleteBulletin(ServiceCall call)
    {
        var (actor, alliance) = await RequireAllianceDirector(call);
        var bulletinId = Number(call.Arg(0));
        if (bulletinId > 0)
            await store.DeleteAllianceBulletinAsync(alliance, actor, bulletinId, call.Cancellation);
        return PyNone.Instance;
    }

    [Method("UpdateBulletinOrder")]
    public async ValueTask<PyValue> UpdateBulletinOrder(ServiceCall call)
    {
        var (actor, alliance) = await RequireAllianceDirector(call);
        await store.UpdateAllianceBulletinOrderAsync(alliance, actor, ContactIds(call.Arg(0)), call.Cancellation);
        return PyNone.Instance;
    }

    private async Task<(long Actor,long Alliance)> RequireAllianceDirector(ServiceCall call)
    {
        var (actor, allianceId) = await RequireAllianceMember(call);
        var character = await characters.GetAsync(actor, call.Cancellation);
        var corporation = character is null
            ? null
            : await store.GetAsync(character.CorporationId, call.Cancellation);
        var member = await store.MemberAsync(actor, call.Cancellation);
        if (corporation is null || corporation.IsNpc || member is null || member.CorporationId != corporation.Id ||
            (CorporationRoles.Session(corporation, member, null,
                await store.TitlesAsync(corporation.Id, call.Cancellation)).AtAll & CorporationRoles.Director) == 0)
            throw AllianceAccessDenied();
        return (actor, allianceId);
    }

    private async Task<(long Actor,long Alliance,long Corporation)> RequireAllianceRole(
        ServiceCall call,long roleMask,bool executorOnly)
    {
        var (actor,allianceId) = await RequireAllianceMember(call);
        var character = await characters.GetAsync(actor,call.Cancellation);
        var corporation = character is null
            ? null
            : await store.GetAsync(character.CorporationId,call.Cancellation);
        var member = await store.MemberAsync(actor,call.Cancellation);
        if (corporation is null || corporation.IsNpc || member?.CorporationId != corporation.Id)
            throw AllianceAccessDenied();
        var roles = CorporationRoles.Session(corporation,member,call.Session.GetInt("stationid"),
            await store.TitlesAsync(corporation.Id,call.Cancellation));
        if ((roles.AtAll & roleMask) == 0)
            throw AllianceAccessDenied();
        if (executorOnly && (await store.AllianceAsync(allianceId,call.Cancellation))?.ActiveExecutorId != corporation.Id)
            throw AllianceAccessDenied();
        return (actor,allianceId,corporation.Id);
    }

    private async Task<(string Key,PyValue Value)[]?> AllianceNotificationFieldsAsync(
        long allianceId, CancellationToken token)
    {
        var alliance = await store.AllianceAsync(allianceId, token);
        if (alliance is null) return null;
        var counts = await store.AllianceCharacterCountsAsync([allianceId], token);
        var prime = await store.AlliancePrimeTimeAsync(allianceId, token);
        var sovereignty = readSovSnapshot is null ? null : await readSovSnapshot(allianceId, token);
        var soleSystemId = sovereignty is null ? null : SoleSovereigntySystemId(sovereignty, allianceId);
        var capital = await store.AllianceCapitalSystemAsync(allianceId, soleSystemId, token);
        return AllianceFields(alliance, counts.GetValueOrDefault(allianceId),
            prime.CurrentHour, prime.NewHour, prime.ValidAfterFileTime, capital.CurrentSystemId);
    }

    private static Descriptor AllianceBulletinDescriptor => CorporationWire.DescriptorOf(
        ("bulletinID", 0x03), ("ownerID", 0x03), ("createDateTime", 0x40), ("editDateTime", 0x40),
        ("editCharacterID", 0x03), ("title", 0x82), ("body", 0x82), ("sortOrder", 0x03));

    private static PyValue AllianceBulletinRow(Descriptor descriptor, CorporationBulletin bulletin) =>
        new PyPackedRow(descriptor,
        [
            new PyInt(bulletin.BulletinId), new PyInt(bulletin.CorporationId),
            new PyLong(bulletin.CreateDateTime), new PyLong(bulletin.EditDateTime),
            new PyInt(bulletin.EditCharacterId), new PyWString(bulletin.Title),
            new PyWString(bulletin.Body), new PyInt(bulletin.SortOrder)
        ]);
}


// Alliance application deadlines, war holds and affiliations use the shared Scheduler, Relations and EventBus.
public sealed class AllianceApplicationDueWorker(
    CorporationStore store,
    PgCharacterStore characters,
    Eve.Scheduler.DurableScheduler scheduler,
    Eve.Relations.RelationStore relations,
    Eve.Events.EventBus events,
    Func<long, CancellationToken, Task> affiliationChanged,CharacterSessions? characterSessions = null)
{
    public const string JobKind = "alliance.application.effective";
    private const string AffiliationSubscriber = "alliance.affiliation.sessions";

    public async Task RegisterAsync(CancellationToken token = default)
    {
        store.CorporationMemberAffiliationWriter = WriteCorporationMemberAffiliationAsync;
        scheduler.Register(JobKind, HandleAsync);
        events.Subscribe(AffiliationSubscriber,
            ["relation.affiliation.corporation.changed", "relation.affiliation.corporation.ended",
             "relation.affiliation.alliance.changed", "relation.affiliation.alliance.ended"],
            NotifyAffiliationChangedAsync);
        await ReconcileCorporationMemberAffiliationsAsync(token);
        await ReconcileAllianceAffiliationsAsync(token);
        foreach (var job in await store.PendingAllianceApplicationJobsAsync(token))
            await scheduler.ScheduleAsync(Spec(job), token);
    }


    private async Task ReconcileCorporationMemberAffiliationsAsync(CancellationToken token)
    {
        var kind = Eve.Relations.RelationKinds.CorporationMember.Name;
        var existing = await relations.ListAsync(kind, token);
        var staleSubjects = existing.Select(relation => relation.Subject).ToHashSet();
        long afterCharacterId = 0;
        while (true)
        {
            var page = await characters.AffiliationsAfterAsync(
                afterCharacterId, Eve.Characters.PgCharacterStore.CorporationAffiliationPageSize, token);
            if (page.Count == 0) break;
            var desired = new Dictionary<long, long?>(page.Count);
            foreach (var affiliation in page)
            {
                if (affiliation.CorporationId <= 0)
                    throw new InvalidOperationException($"character {affiliation.CharacterId} has no valid corporation id");
                desired[affiliation.CharacterId] = affiliation.CorporationId;
                staleSubjects.Remove(affiliation.CharacterId);
            }
            await relations.ReconcileOnePerSubjectBatchAsync(kind, desired,
                "corporation membership reconciliation", token);
            afterCharacterId = page[^1].CharacterId;
        }

        foreach (var batch in staleSubjects.Order().Chunk(Eve.Relations.RelationStore.ReconciliationBatchSize))
        {
            var desired = batch.ToDictionary(subject => subject, _ => (long?)null);
            await relations.ReconcileOnePerSubjectBatchAsync(kind, desired,
                "stale corporation membership reconciliation", token);
        }
    }

    private async Task ReconcileAllianceAffiliationsAsync(CancellationToken token)
    {
        var kind = Eve.Relations.RelationKinds.AllianceMember.Name;
        var desired = (await store.CorporationAllianceAffiliationsAsync(token))
            .ToDictionary(affiliation => affiliation.CorporationId,
                affiliation => affiliation.AllianceId is > 0 ? affiliation.AllianceId : null);
        foreach (var relation in await relations.ListAsync(kind, token))
            desired.TryAdd(relation.Subject, null);

        foreach (var batch in desired.OrderBy(pair => pair.Key).Chunk(Eve.Relations.RelationStore.ReconciliationBatchSize))
        {
            var changes = batch.ToDictionary(pair => pair.Key, pair => pair.Value);
            await relations.ReconcileOnePerSubjectBatchAsync(kind, changes,
                "alliance affiliation reconciliation", token);
        }
    }

    public async Task ScheduleAcceptedAsync(System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction, AllianceApplicationDueJob job, CancellationToken token)
    {
        await scheduler.ScheduleAsync((Npgsql.NpgsqlConnection)connection, (Npgsql.NpgsqlTransaction)transaction,
            Spec(job), token);
    }

    public void Wake() => scheduler.Wake();

    private async Task WriteCorporationMemberAffiliationAsync(System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction, long characterId, long corporationId, CancellationToken token)
    {
        await relations.SetAsync((Npgsql.NpgsqlConnection)connection, (Npgsql.NpgsqlTransaction)transaction,
            Eve.Relations.RelationKinds.CorporationMember.Name, characterId, corporationId, 1,
            "corporation membership", cancellationToken: token);
    }

    public async Task WriteAffiliationAsync(System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction, long corporationId, long? allianceId, CancellationToken token)
    {
        var kind = Eve.Relations.RelationKinds.AllianceMember.Name;
        var pgConnection = (Npgsql.NpgsqlConnection)connection;
        var pgTransaction = (Npgsql.NpgsqlTransaction)transaction;
        if (allianceId is > 0)
        {
            await relations.SetAsync(pgConnection, pgTransaction, kind, corporationId, allianceId.Value, 1,
                "alliance membership", cancellationToken: token);
            return;
        }

        foreach (var current in await relations.FromAsync(kind, corporationId, pgConnection, pgTransaction, token))
            await relations.EndAsync(pgConnection, pgTransaction, kind, corporationId, current.Object,
                "alliance membership ended", token);
    }

    private async Task<IReadOnlyList<AllianceApplicationWar>> ReadWarsAsync(
        long corporationId, long allianceId, CancellationToken token)
    {
        var outgoing = await relations.FromAsync(Eve.Relations.RelationKinds.War.Name, corporationId, cancellationToken: token);
        var incoming = await relations.ToAsync(Eve.Relations.RelationKinds.War.Name, corporationId, cancellationToken: token);
        return outgoing.Concat(incoming)
            .DistinctBy(war => war.Id)
            .Where(war => war.UntilMs is null || war.UntilMs > scheduler.Now)
            .Select(war =>
            {
                var attrs = war.Attrs<System.Text.Json.JsonElement>();
                var mutual = attrs.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    attrs.TryGetProperty("mutual", out var value) &&
                    value.ValueKind == System.Text.Json.JsonValueKind.True;
                return new AllianceApplicationWar(war.Subject, war.Object, mutual);
            })
            .ToArray();
    }

    private async Task HandleAsync(Eve.Scheduler.JobContext job, CancellationToken token)
    {
        var payload = job.Payload<AllianceApplicationDueJob>()
            ?? throw new InvalidOperationException("alliance application job has no payload");
        var result = await store.ProcessDueAllianceApplicationAsync(
            job.Connection, job.Transaction, payload, ReadWarsAsync, WriteAffiliationAsync, token);
        if (result.RescheduleAtMs is { } at)
            job.RunAgainAt(at, payload with { DueAtMs = at });
    }

    private async Task NotifyAffiliationChangedAsync(Eve.Events.EventContext context, CancellationToken token)
    {
        var change = context.Event.Payload<Eve.Relations.RelationChange>()
            ?? throw new InvalidOperationException("affiliation event has no payload");
        var current = await relations.FromAsync(change.Kind, change.Subject,
            (Npgsql.NpgsqlConnection)context.Connection, (Npgsql.NpgsqlTransaction)context.Transaction, token);
        var ended = context.Event.Topic.EndsWith(".ended", StringComparison.Ordinal);
        if (ended ? current.Count != 0 : !current.Any(relation => relation.Object == change.Object))
            return;

        if (change.Kind == Eve.Relations.RelationKinds.CorporationMember.Name)
        {
            await affiliationChanged(change.Subject, token);
            return;
        }
        if (change.Kind == Eve.Relations.RelationKinds.AllianceMember.Name)
        {
            foreach (var character in await characters.ForCorporationAsync(change.Subject, token))
                await affiliationChanged(character.Id, token);
            if (!ended && characterSessions is not null)
            {
                var alliance = await store.AllianceAsync(change.Object,token);
                var member = (await store.AllianceMembersAsync(change.Object,token))
                    .SingleOrDefault(row => row.CorporationId == change.Subject);
                if (alliance is not null && member is not null && member.StartedMs != alliance.CreatedMs)
                {
                    var counts = await store.AllianceCharacterCountsAsync([alliance.Id],token);
                    var prime = await store.AlliancePrimeTimeAsync(alliance.Id,token);
                    var capital = await store.AllianceCapitalSystemAsync(alliance.Id,null,token);
                    var fields = AllianceRegistryService.AllianceFields(alliance,
                        counts.GetValueOrDefault(alliance.Id),prime.CurrentHour,prime.NewHour,
                        prime.ValidAfterFileTime,capital.CurrentSystemId);
                    CorporationNotificationAdapter.AllianceChanged(
                        characterSessions,alliance.Id,null,fields);
                    CorporationNotificationAdapter.AllianceMemberChanged(
                        characterSessions,alliance.Id,change.Subject,null,member);
                    CorporationNotificationAdapter.CorporationChanged(
                        characterSessions,change.Subject,"allianceID",PyNone.Instance,new PyInt(alliance.Id));
                }
            }
            return;
        }
        throw new InvalidOperationException($"unexpected affiliation event kind '{change.Kind}'");
    }

    private static Eve.Scheduler.JobSpec Spec(AllianceApplicationDueJob job) =>
        new(JobKind, job.DueAtMs)
        {
            Key = $"alliance-application:{job.ApplicationId}",
            Payload = job,
            OnDuplicate = Eve.Scheduler.OnDuplicate.Replace
        };
}

// CorpRegistry Alliance RPCs follow EveJS v0.12.9 corpRegistryRuntime.js (828823a75f211fdecb945b087a26c225f111491f).
public sealed partial class CorpRegistryService
{
    private static readonly Descriptor AllianceApplicationDescriptor = CorporationWire.DescriptorOf(
        ("allianceID",3),("corporationID",3),("applicationText",130),("state",3),("applicationDateTime",64));

    private async Task<long> RequireAllianceApplicationDirector(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor ||
            await characters.GetAsync(actor,call.Cancellation) is not { } character ||
            await store.GetAsync(character.CorporationId,call.Cancellation) is not { IsNpc:false } corporation)
            throw CorporationAccessDenied();
        if (corporation.CeoId != actor)
        {
            var member = await store.MemberAsync(actor,call.Cancellation);
            if (member is null || member.CorporationId != corporation.Id ||
                (CorporationRoles.Session(corporation,member,null,await store.TitlesAsync(corporation.Id,call.Cancellation)).AtAll & CorporationRoles.Director) == 0)
                throw CorporationAccessDenied();
        }
        return corporation.Id;
    }

    [Method("CreateAlliance")]
    public async ValueTask<PyValue> CreateAlliance(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor ||
            await characters.GetAsync(actor, call.Cancellation) is not { } character ||
            await store.GetAsync(character.CorporationId, call.Cancellation) is not { IsNpc: false } corporation ||
            corporation.CeoId != actor)
            throw CorporationAccessDenied();

        var wasAlreadyInAlliance = corporation.AllianceId is not null;
        var request = new NewAlliance(
            JsSemantics.StringValue(call.Arg(0)) ?? "", JsSemantics.StringValue(call.Arg(1)) ?? "",
            JsSemantics.StringValue(call.Arg(2)) ?? "", JsSemantics.StringValue(call.Arg(3)) ?? "");
        var operationKey = $"alliance-create:{call.Session.SessionId}:{actor}:{Number(call.CallId)}";
        if (store.AllianceAffiliationWriter is not { } writer)
            throw new CorporationError("ALLIANCE_AFFILIATION_UNAVAILABLE");
        var alliance = await store.CreateAllianceAsync(actor, request, operationKey, writer, call.Cancellation);
        if (store.AllianceMaintenanceBillWriter is { } writeMaintenanceBill)
            await writeMaintenanceBill(alliance.Id, call.Cancellation);
        if (!wasAlreadyInAlliance)
        {
            var counts = await store.AllianceCharacterCountsAsync([alliance.Id],call.Cancellation);
            var fields = AllianceRegistryService.AllianceFields(alliance,counts.GetValueOrDefault(alliance.Id));
            var member = (await store.AllianceMembersAsync(alliance.Id,call.Cancellation))
                .Single(item => item.CorporationId == corporation.Id);
            CorporationNotificationAdapter.AllianceChanged(characterSessions,call,alliance.Id,null,fields);
            CorporationNotificationAdapter.AllianceMemberChanged(
                characterSessions,call,alliance.Id,corporation.Id,null,member);
            CorporationNotificationAdapter.CorporationChanged(
                characterSessions,call,corporation.Id,"allianceID",PyNone.Instance,new PyInt(alliance.Id));
        }
        return new PyInt(alliance.Id);
    }

    [Method("GetAllianceApplications")]
    public async ValueTask<PyValue> GetAllianceApplications(ServiceCall call)
    {
        var corporationId = await RequireAllianceApplicationDirector(call);
        var applications = await store.AllianceApplicationsForCorporationAsync(corporationId,call.Cancellation);
        return new PyObjectEx(ObjectExKind.Ex2,
            Py.Tuple(Py.Tuple(new PyToken("carbon.common.script.sys.crowset.CIndexedRowset")),
                Py.Dict(("header",AllianceApplicationDescriptor.Header),("idName",new PyStr("allianceID")),
                    ("columnName",new PyStr("allianceID")))),[],
            [.. applications.Select(application => new KeyValuePair<PyValue,PyValue>(
                new PyInt(application.AllianceId),new PyPackedRow(AllianceApplicationDescriptor,[
                    new PyInt(application.AllianceId),new PyInt(application.CorporationId),new PyWString(application.Text),
                    new PyInt(application.Status),CorporationWire.Time(application.CreatedMs)])))]);
    }

    [Method("GetSuggestedAllianceShortNames")]
    public ValueTask<PyValue> GetSuggestedAllianceShortNames(ServiceCall call)
    {
        var normalized = new string((JsSemantics.StringValue(call.Arg(0)) ?? "")
            .Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).Take(5).ToArray());
        var basis = normalized.Length == 0 ? "ALLY" : normalized;
        var suggestions = new[] { basis,basis[..Math.Min(4,basis.Length)],basis[..Math.Min(3,basis.Length)] + "X" }
            .Select(value => value.Length == 0 ? "ALLY" : value).Distinct(StringComparer.Ordinal);
        return ValueTask.FromResult<PyValue>(new PyList([.. suggestions.Select(value =>
            (PyValue)Py.KeyVal(("shortName",new PyStr(value))))]));
    }
}


public sealed class AllianceMaintenanceBillWorker(
    CorporationStore store,
    Eve.Corporations.CorporationOfficeStore officeStore,
    Eve.Scheduler.DurableScheduler scheduler,
    Func<long> now)
{
    private const int BillTypeId = 5;
    private const long FiletimeEpochOffsetMs = 11_644_473_600_000L;
    private const long RetryDelayMs = 24L * 60 * 60 * 1000;
    private const string JobKind = "alliance-maintenance-bill";

    public async Task RegisterAsync(CancellationToken token = default)
    {
        scheduler.Register(JobKind, HandleAsync);
        store.AllianceMaintenanceBillWriter = EnsureAndScheduleAsync;
        await ReconcileAsync(token);
    }

    public async Task ReconcileAsync(CancellationToken token = default)
    {
        foreach (var alliance in await store.AllAlliancesAsync(token))
            if ((await store.AllianceMaintenanceBillsAsync(alliance.Id, token)).Count == 0)
                await EnsureAndScheduleAsync(alliance.Id, token);

        foreach (var bill in await store.PendingAllianceMaintenanceBillsAsync(token))
            await scheduler.ScheduleAsync(Spec(bill), token);
    }

    private async Task EnsureAndScheduleAsync(long allianceId, CancellationToken token)
    {
        var bill = await store.EnsureInitialAllianceMaintenanceBillAsync(allianceId, token);
        if (bill is { Paid: false })
            await scheduler.ScheduleAsync(Spec(bill), token);
    }

    private async Task HandleAsync(Eve.Scheduler.JobContext job, CancellationToken token)
    {
        var payload = job.Payload<AllianceMaintenanceBillDue>()
            ?? throw new InvalidOperationException("alliance maintenance bill job has no payload");
        var bill = (await store.AllianceMaintenanceBillsAsync(payload.AllianceId, token))
            .SingleOrDefault(entry => entry.BillId == payload.BillId);
        if (bill is null) return;

        var dueAtMs = UnixMilliseconds(bill.DueFiletime);
        if (dueAtMs > now())
        {
            job.RunAgainAt(dueAtMs, payload);
            return;
        }
        if (bill.Paid)
        {
            await RenewAsync(bill, token);
            return;
        }

        var alliance = await store.AllianceAsync(payload.AllianceId, token);
        if (alliance is not { Deleted: false } || alliance.ActiveExecutorId is not { } executorCorporationId)
            return;
        var allianceSettings = await officeStore.AutomaticPaySettingsAsync([alliance.Id], token);
        if (allianceSettings.TryGetValue(alliance.Id, out var payerPolicy) &&
            payerPolicy.EnabledBillTypes.GetValueOrDefault(BillTypeId))
        {
            var executorSettings = await officeStore.AutomaticPaySettingsAsync([executorCorporationId], token);
            var accountKey = executorSettings.TryGetValue(executorCorporationId, out var settings) &&
                settings.DivisionId is >= 1000 and <= 1006 ? settings.DivisionId : 1000;
            if (await store.PayAllianceMaintenanceBillAsync(
                    bill.BillId, alliance.Id, executorCorporationId, accountKey, now(), token))
            {
                await RenewAsync(bill, token);
                return;
            }
        }

        // shortcut: unpaid-bill closure is pending the durable sovereignty and war transfer lifecycle.
        job.RunAgainAt(checked(now() + RetryDelayMs), payload);
    }

    private async Task RenewAsync(CorporationBill paidBill, CancellationToken token)
    {
        var next = await store.CreateNextAllianceMaintenanceBillAsync(
            paidBill.DebtorId, paidBill.DueFiletime, token);
        if (next is not null)
            await scheduler.ScheduleAsync(Spec(next), token);
    }

    private static Eve.Scheduler.JobSpec Spec(CorporationBill bill) =>
        new(JobKind, UnixMilliseconds(bill.DueFiletime))
        {
            Key = $"alliance-maintenance:{bill.BillId}",
            Payload = new AllianceMaintenanceBillDue(bill.DebtorId, bill.BillId),
            OnDuplicate = Eve.Scheduler.OnDuplicate.Replace
        };

    private static long UnixMilliseconds(long filetime) =>
        Math.Max(0, filetime / 10_000L - FiletimeEpochOffsetMs);

    private sealed record AllianceMaintenanceBillDue(long AllianceId, long BillId);
}
